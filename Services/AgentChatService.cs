using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeviceStatusBeacon.Services;

/// <summary>
/// Agent 对话服务。
/// </summary>
public sealed class AgentChatService(HttpClient httpClient, IDeviceStatusQueryService deviceStatusQueryService) : IAgentChatService {
	private const int MaxToolRounds = 8;
	private const int MaxToolCalls = 8;
	private const int MaxDeviceCount = 20;
	private const int MaxDeviceLogCount = 50;
	private const int MaxHistoryMessageCount = 80;
	private const int MaxHistoryCharacterCount = 200_000;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private static readonly string SystemPrompt = $"""
		你是设备状态信标系统中的只读诊断助手，运行在设备状态信标系统中。
		你只能根据系统提供的工具查询设备、日志和概览信息，不能编造未查询到的数据，也不能要求或暗示用户执行危险操作。
		你只能分析当前登录用户有权查看的数据范围，你拿到的数据都是经过权限过滤的，不能也无法绕过权限读取当前用户无权读取的数据。
		当用户询问设备、日志、系统运行状况、近期活动、异常迹象时，应优先调用工具获取事实。
		除非用户另有要求，回答必须使用纯文本，不要使用 Markdown 表格、代码块、标题语法或嵌套列表。
		当前交互式前端不支持 Markdown 和 HTML 渲染，因此请不要使用任何 Markdown 或 HTML 语法。
		如果需要分点，请使用简短自然段，或使用“1. 2. 3.”这样的纯文本编号。
		结论应克制、可核查；如果信息不足，请说明还需要查询哪类设备或日志。
		默认情况下，使用用户的语言回答问题，除非用户明确要求使用其他语言，如果用户的语言不明确，默认使用简体中文。

		你在单轮对话中最多可以调用 {MaxToolRounds} 次工具，每次最多调用 {MaxToolCalls} 个工具，如果未能在限制内生成最终回答，请提前停止，明确告诉用户。
		其中“单轮对话”是指从用户发起一次请求开始，一直到你返回最终的没有工具调用的最终消息为止。如果用户与你多轮对话，在不同轮次之间，该限制会重置。
		使用尽可能务实且友好的语气回答用户问题，减少各类语气词、不必要的表达，避免使用“我认为”“我猜”“我建议”等措辞，除非你确实无法获取任何事实数据。

		对于日志，如果提交者 ID 为 null，则可能提交者为设备自身或提交用户已不存在，请不要在结论中暗示或推测提交者身份。
		如果用户明确问起，请说明日志的提交者 ID 为 null，提交者可能是设备自身或提交用户已被删除，但不要推测具体身份。
		""";

	/// <inheritdoc/>
	public async Task ProcessAsync(
		ClaimsPrincipal principal,
		AgentChatRequest request,
		Func<string, object?, CancellationToken, Task> writeEventAsync,
		CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(writeEventAsync);

		if (request.Provider is null) {
			throw new AgentChatException("模型供应商配置不能为空。");
		}
		if (request.Messages is null) {
			throw new AgentChatException("对话消息不能为空。");
		}

		ValidateProvider(request.Provider);

		// 查询会话仍然由既有查询服务创建，确保 Agent 和普通页面共享同一套权限范围
		var session = deviceStatusQueryService.CreateQuerySessionAsync(principal);

		// 前端会回传完整模型消息历史，后端只在本次请求内补上系统提示词
		var workingMessages = BuildInitialMessages(request.Messages);

		// newMessages 只记录本轮新增的 assistant/tool 消息，返回给前端后由前端拼入 localStorage 历史
		var newMessages = new JsonArray();

		await writeEventAsync("status", new {
			text = "正在调用模型。"
		}, cancellationToken);

		for (var round = 0; round < MaxToolRounds; round++) {
			// 每一轮都让模型基于已有 user/assistant/tool 消息决定是否继续调用工具
			var assistantMessage = await CompleteAsync(request.Provider, workingMessages, cancellationToken);
			var assistantMessageForHistory = CloneObject(assistantMessage);

			// workingMessages 用于本次服务端工具循环，newMessages 用于回传给浏览器持久化
			workingMessages.Add(CloneObject(assistantMessage));
			newMessages.Add(assistantMessageForHistory);

			if (assistantMessage["tool_calls"] is not JsonArray toolCalls || toolCalls.Count == 0) {
				// 没有工具调用时，本轮已经得到最终回复，可以把完整新增模型消息交还给前端
				var finalText = assistantMessage["content"]?.GetValue<string>() ?? string.Empty;
				await writeEventAsync("assistant_message", new {
					text = finalText
				}, cancellationToken);

				await writeEventAsync("model_messages", new {
					messages = newMessages
				}, cancellationToken);

				await writeEventAsync("done", null, cancellationToken);
				return;
			}

			// 针对每一次请求做工具调用次数限制，单轮对话最多为 MaxToolCalls * MaxToolRounds 次工具调用，避免模型或兼容接口异常时陷入长时间工具循环
			var toolCallCount = 0;

			foreach (var toolCallNode in toolCalls) {
				cancellationToken.ThrowIfCancellationRequested();

				if (toolCallNode is not JsonObject toolCall) {
					continue;
				}

				toolCallCount++;
				if (toolCallCount > MaxToolCalls) {
					// 限制单轮工具总数，避免模型或兼容接口异常时陷入长时间工具循环
					throw new AgentChatException("工具调用次数过多，已停止本轮分析。");
				}

				var toolCallId = toolCall["id"]?.GetValue<string>() ?? string.Empty;
				var function = toolCall["function"] as JsonObject;
				var toolName = function?["name"]?.GetValue<string>() ?? string.Empty;
				var argumentsJson = function?["arguments"]?.GetValue<string>() ?? "{}";

				await writeEventAsync("tool_started", new {
					toolCallId,
					name = toolName,
					argumentsSummary = CreateArgumentsSummary(argumentsJson)
				}, cancellationToken);

				// 工具结果同时用于展示和模型上下文，因此这里记录耗时并保留完整 content
				var startedAt = TimeProvider.System.GetTimestamp();
				var toolResult = await ExecuteToolAsync(session, toolName, argumentsJson, cancellationToken);
				var elapsedMs = (long)TimeProvider.System.GetElapsedTime(startedAt).TotalMilliseconds;

				// tool 消息必须携带 tool_call_id，前端下一轮回传时模型才能正确对应历史工具调用
				var toolMessage = new JsonObject {
					["role"] = "tool",
					["tool_call_id"] = toolCallId,
					["content"] = toolResult.Content
				};

				workingMessages.Add(CloneObject(toolMessage));
				newMessages.Add(CloneObject(toolMessage));

				await writeEventAsync("tool_finished", new {
					toolCallId,
					name = toolName,
					status = toolResult.Success ? "success" : "error",
					summary = toolResult.Summary,
					elapsedMs
				}, cancellationToken);
			}

			await writeEventAsync("status", new {
				text = "正在根据查询结果生成结论。"
			}, cancellationToken);
		}

		throw new AgentChatException("模型连续请求工具调用，未能在限制内生成最终回答。");
	}

	/// <summary>
	/// 调用 OpenAI-compatible Chat Completions 接口。
	/// </summary>
	/// <param name="provider">模型供应商配置</param>
	/// <param name="messages">消息列表</param>
	/// <param name="cancellationToken">取消令牌</param>
	/// <returns>模型返回的 assistant 消息</returns>
	private async Task<JsonObject> CompleteAsync(AgentProviderSettings provider, JsonArray messages, CancellationToken cancellationToken) {
		var body = new JsonObject {
			["model"] = provider.Model.Trim(),
			["messages"] = CloneArray(messages),
			["tools"] = BuildToolDefinitions(),
			["tool_choice"] = "auto",
			["stream"] = false
		};

		using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(provider.Endpoint)) {
			Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
		};

		// 供应商配置由当前用户前端提交，本服务不保存 API Key，也不把 Key 写入任何日志或数据库
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey.Trim());
		request.Headers.Accept.Add(new("application/json"));

		// 只读取响应头后再读正文，便于后续如果需要可以更早处理错误状态
		using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode) {
			// 兼容不同中转站和模型服务的错误 JSON 形态，尽量提取人类可读消息
			throw new AgentChatException($"模型接口返回 {(int)response.StatusCode}：{NormalizeErrorText(responseText)}");
		}

		// 当前只支持 OpenAI-compatible Chat Completions 的 choices[0].message 响应结构
		var root = JsonNode.Parse(responseText) as JsonObject
			?? throw new AgentChatException("模型接口返回内容不是合法 JSON 对象。");
		var message = root["choices"]?[0]?["message"] as JsonObject
			?? throw new AgentChatException("模型接口返回内容缺少 assistant 消息。");

		return NormalizeAssistantMessage(message);
	}

	/// <summary>
	/// 执行 Agent 工具。
	/// </summary>
	/// <param name="session">查询会话</param>
	/// <param name="toolName">工具名称</param>
	/// <param name="argumentsJson">工具参数 JSON</param>
	/// <param name="cancellationToken">取消令牌</param>
	/// <returns>工具执行结果</returns>
	private async Task<AgentToolResult> ExecuteToolAsync(DeviceStatusQuerySession session, string toolName, string argumentsJson, CancellationToken cancellationToken) {
		try {
			// 模型返回的 function.arguments 是 JSON 字符串，先统一解析再交给具体工具读取
			using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
			var arguments = argumentsDocument.RootElement;

			// 所有工具都只走查询服务，不直接访问 DbContext，也不提供任何写入能力
			return toolName switch {
				"get_system_time" => GetSystemTime(),
				"get_system_overview" => await GetSystemOverviewAsync(session, cancellationToken),
				"list_devices" => await ListDevicesAsync(session, arguments, cancellationToken),
				"get_device_details" => await GetDeviceDetailsAsync(session, arguments, cancellationToken),
				"get_device_logs" => await GetDeviceLogsAsync(session, arguments, cancellationToken),
				"get_log_details" => await GetLogDetailsAsync(session, arguments, cancellationToken),
				_ => CreateToolError($"未知工具：{toolName}")
			};
		} catch (JsonException) {
			// 参数解析失败作为工具结果返回给模型，让模型有机会修正参数后继续调用
			return CreateToolError("工具参数不是合法 JSON。");
		}
	}

	/// <summary>
	/// 获取系统当前 UTC 时间。
	/// </summary>
	/// <returns>工具执行结果</returns>
	private static AgentToolResult GetSystemTime() {
		// 时间工具固定使用 UTC，避免引入时区配置和前端本地时区差异
		var utcNow = DateTime.UtcNow;
		var result = new {
			TimeZone = "UTC",
			UtcNow = FormatDateTime(utcNow)
		};

		return CreateToolSuccess(result, $"{utcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}");
	}

	/// <summary>
	/// 获取系统概览。
	/// </summary>
	private async Task<AgentToolResult> GetSystemOverviewAsync(DeviceStatusQuerySession session, CancellationToken cancellationToken) {
		// 概览工具复用 Dashboard 首屏数据和延迟加载活动数据，避免重复散写统计规则
		var overview = await deviceStatusQueryService.GetDashboardOverviewAsync(session, cancellationToken);
		var activity = await deviceStatusQueryService.GetDashboardActivityAsync(session, cancellationToken);

		// result 是给模型使用的完整结构化数据，summary 是给前端工具概览展示的短文本
		var result = new {
			overview.Session,
			overview.AccessibleDeviceCount,
			overview.EnabledDeviceCount,
			overview.RecentActiveDeviceCount,
			overview.RecentActiveWindowHours,
			activity.AccessibleLogCount,
			RecentDeviceActivities = activity.RecentDeviceActivities.Select(MapDeviceActivity)
		};

		return CreateToolSuccess(
			result,
			$"可见设备 {overview.AccessibleDeviceCount} 台，启用 {overview.EnabledDeviceCount} 台，近 {overview.RecentActiveWindowHours} 小时活跃 {overview.RecentActiveDeviceCount} 台，可见日志 {activity.AccessibleLogCount} 条。");
	}

	/// <summary>
	/// 查询设备列表。
	/// </summary>
	private async Task<AgentToolResult> ListDevicesAsync(DeviceStatusQuerySession session, JsonElement arguments, CancellationToken cancellationToken) {
		// 设备列表工具控制最大返回数量，防止模型一次拉取过多数据造成上下文膨胀
		var searchTerm = GetString(arguments, "searchTerm");
		var take = GetBoundedInt(arguments, "take", 10, 1, MaxDeviceCount);
		var devices = await deviceStatusQueryService.GetDeviceSliceAsync(session, searchTerm, take, cancellationToken: cancellationToken);
		var result = new {
			SearchTerm = searchTerm,
			Take = take,
			Devices = devices.Select(MapDeviceSummary)
		};

		return CreateToolSuccess(result, $"返回 {devices.Count} 台设备。");
	}

	/// <summary>
	/// 获取单设备详情。
	/// </summary>
	private async Task<AgentToolResult> GetDeviceDetailsAsync(DeviceStatusQuerySession session, JsonElement arguments, CancellationToken cancellationToken) {
		// 单设备详情要求明确设备名称，避免模型在名称不确定时误查到无关设备
		var deviceName = GetRequiredString(arguments, "deviceName");
		if (deviceName is null) {
			return CreateToolError("缺少设备名称 deviceName。");
		}

		// 查询服务会按当前会话权限过滤，找不到时统一暴露为未找到或无权读取
		var details = await deviceStatusQueryService.GetDeviceDetailsByNameAsync(session, deviceName, cancellationToken);
		if (details is null) {
			return CreateToolSuccess(new {
				DeviceName = deviceName,
				Found = false
			}, $"未找到设备 {deviceName}，或当前用户无权读取。");
		}

		var result = new {
			Found = true,
			Device = MapDeviceSummary(details.Device),
			RecentLogs = details.RecentLogs.Select(MapOnlineLogSummary)
		};

		return CreateToolSuccess(result, $"已读取设备 {details.Device.DeviceName} 的详情和最近 {details.RecentLogs.Count} 条日志。");
	}

	/// <summary>
	/// 获取单设备最近日志。
	/// </summary>
	private async Task<AgentToolResult> GetDeviceLogsAsync(DeviceStatusQuerySession session, JsonElement arguments, CancellationToken cancellationToken) {
		// 日志查询同样要求设备名称，并限制 take，保证历史会话体积可控
		var deviceName = GetRequiredString(arguments, "deviceName");
		if (deviceName is null) {
			return CreateToolError("缺少设备名称 deviceName。");
		}

		var take = GetBoundedInt(arguments, "take", 5, 1, MaxDeviceLogCount);
		var logs = await deviceStatusQueryService.GetLogsByDeviceNameAsync(session, deviceName, take, cancellationToken);
		var result = new {
			DeviceName = deviceName,
			Take = take,
			Logs = logs.Select(MapOnlineLogSummary)
		};

		return CreateToolSuccess(result, $"设备 {deviceName} 返回 {logs.Count} 条最近日志。");
	}

	/// <summary>
	/// 获取单条日志详情。
	/// </summary>
	private async Task<AgentToolResult> GetLogDetailsAsync(DeviceStatusQuerySession session, JsonElement arguments, CancellationToken cancellationToken) {
		// 日志详情使用数值 ID，允许模型传入字符串形式以兼容不同模型的参数生成习惯
		var onlineLogId = GetLong(arguments, "onlineLogId");
		if (onlineLogId is null) {
			return CreateToolError("缺少日志 ID onlineLogId。");
		}

		var log = await deviceStatusQueryService.GetOnlineLogDetailsAsync(session, onlineLogId.Value, cancellationToken);
		if (log is null) {
			return CreateToolSuccess(new {
				OnlineLogId = onlineLogId,
				Found = false
			}, $"未找到日志 {onlineLogId}，或当前用户无权读取。");
		}

		return CreateToolSuccess(new {
			Found = true,
			Log = MapOnlineLogDetails(log)
		}, $"已读取日志 {log.OnlineLogId} 的详情。");
	}

	private static JsonArray BuildInitialMessages(IReadOnlyCollection<JsonElement> incomingMessages) {
		// 后端保持无状态，因此请求体必须至少包含本轮用户消息
		if (incomingMessages.Count == 0) {
			throw new AgentChatException("对话消息不能为空。");
		}
		if (incomingMessages.Count > MaxHistoryMessageCount) {
			throw new AgentChatException("对话历史过长，请减少历史消息后重试。");
		}

		var characterCount = 0;

		// 系统提示词每次请求临时注入，不进入前端 localStorage，也不会被用户历史覆盖
		var messages = new JsonArray {
			new JsonObject {
				["role"] = "system",
				["content"] = SystemPrompt
			}
		};

		foreach (var message in incomingMessages) {
			// 以原始 JSON 文本长度做简单上限保护，避免 localStorage 历史过大导致代理请求失控
			characterCount += message.GetRawText().Length;
			if (characterCount > MaxHistoryCharacterCount) {
				throw new AgentChatException("对话历史内容过长，请裁剪历史后重试。");
			}

			// 这里只允许前端回传 Chat Completions 历史消息，不接受其他任意 JSON 形态
			var messageObject = JsonNode.Parse(message.GetRawText()) as JsonObject
				?? throw new AgentChatException("对话消息格式无效。");
			var role = messageObject["role"]?.GetValue<string>();
			if (role is not ("user" or "assistant" or "tool")) {
				throw new AgentChatException("对话历史只能包含 user、assistant 和 tool 消息。");
			}

			messages.Add(messageObject);
		}

		return messages;
	}

	private static JsonArray BuildToolDefinitions() => [
		// 工具定义保持少而明确，方便兼容 DeepSeek、MiMo 和常见 OpenAI-compatible 中转站
		CreateToolDefinition(
			"get_system_time",
			"获取系统当前 UTC 时间。此工具不需要任何参数。",
			new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject(),
				["additionalProperties"] = false
			}),
		CreateToolDefinition(
			"get_system_overview",
			"获取当前用户可见范围内的系统概览、设备数量、日志数量和近期活跃设备。",
			new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject(),
				["additionalProperties"] = false
			}),
		CreateToolDefinition(
			"list_devices",
			"按设备名称或显示名称筛选设备列表，用于查找设备和确认总体设备状态。",
			new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject {
					["searchTerm"] = new JsonObject {
						["type"] = "string",
						["description"] = "设备名称或显示名称筛选关键字，可省略。"
					},
					["take"] = new JsonObject {
						["type"] = "integer",
						["description"] = "返回数量，范围 1 到 20。"
					}
				},
				["additionalProperties"] = false
			}),
		CreateToolDefinition(
			"get_device_details",
			"按设备名称获取单个设备的最新状态和最近日志摘要。",
			new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject {
					["deviceName"] = new JsonObject {
						["type"] = "string",
						["description"] = "设备名称。"
					}
				},
				["required"] = new JsonArray("deviceName"),
				["additionalProperties"] = false
			}),
		CreateToolDefinition(
			"get_device_logs",
			"按设备名称获取该设备最近的在线日志。",
			new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject {
					["deviceName"] = new JsonObject {
						["type"] = "string",
						["description"] = "设备名称。"
					},
					["take"] = new JsonObject {
						["type"] = "integer",
						["description"] = "返回日志数量，范围 1 到 50；如果没有长流程回溯的需求，建议控制在 10 以内。"
					}
				},
				["required"] = new JsonArray("deviceName"),
				["additionalProperties"] = false
			}),
		CreateToolDefinition(
			"get_log_details",
			"按在线日志 ID 获取单条日志的完整详情。",
			new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject {
					["onlineLogId"] = new JsonObject {
						["type"] = "integer",
						["description"] = "在线日志 ID。"
					}
				},
				["required"] = new JsonArray("onlineLogId"),
				["additionalProperties"] = false
			})
	];

	private static JsonObject CreateToolDefinition(string name, string description, JsonObject parameters) =>
		new() {
			["type"] = "function",
			["function"] = new JsonObject {
				["name"] = name,
				["description"] = description,
				["parameters"] = parameters
			}
		};

	private static JsonObject NormalizeAssistantMessage(JsonObject message) {
		// 部分兼容接口在工具调用消息中省略 content，这里补 null 以保持前端历史结构稳定
		var normalized = CloneObject(message);
		normalized["role"] = "assistant";

		if (!normalized.ContainsKey("content")) {
			normalized["content"] = null;
		}

		return normalized;
	}

	private static void ValidateProvider(AgentProviderSettings provider) {
		// Provider 由前端提交，每次请求都要重新校验，避免无效地址进入 HttpClient
		if (string.IsNullOrWhiteSpace(provider.Endpoint)) {
			throw new AgentChatException("模型接口终结点不能为空。");
		}
		if (!Uri.TryCreate(BuildChatCompletionsUri(provider.Endpoint), UriKind.Absolute, out var uri)
			|| uri.Scheme is not ("http" or "https")) {
			throw new AgentChatException("模型接口终结点不是合法的 HTTP 地址。");
		}
		if (string.IsNullOrWhiteSpace(provider.Model)) {
			throw new AgentChatException("模型名称不能为空。");
		}
		if (string.IsNullOrWhiteSpace(provider.ApiKey)) {
			throw new AgentChatException("API Key 不能为空。");
		}
	}

	private static string BuildChatCompletionsUri(string endpoint) {
		// 允许用户填写基地址，也允许直接填写完整的 /chat/completions 地址
		var normalizedEndpoint = endpoint.Trim().TrimEnd('/');
		return normalizedEndpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
			? normalizedEndpoint
			: $"{normalizedEndpoint}/chat/completions";
	}

	private static AgentToolResult CreateToolSuccess(object result, string summary) {
		// Content 是完整工具结果，前端会持久化到 tool 消息供下一轮模型继续使用
		return new(true, JsonSerializer.Serialize(result, JsonOptions), summary);
	}

	private static AgentToolResult CreateToolError(string message) {
		// 工具错误也作为 JSON 内容返回给模型，保持 function calling 协议形态一致
		return new(false, JsonSerializer.Serialize(new {
			Error = message
		}, JsonOptions), message);
	}

	private static object MapDeviceSummary(DeviceSummary device) => new {
		device.DeviceId,
		device.DeviceName,
		device.DisplayName,
		device.Enabled,
		LatestLogTime = FormatDateTime(device.LatestLogTime),
		LatestReportedAddresses = device.LatestReportedAddresses?.Select(address => address.ToString()).ToArray() ?? [],
		LatestReporterRemoteAddress = device.LatestReporterRemoteAddress?.ToString()
	};

	private static object MapDeviceActivity(DeviceActivitySummary device) => new {
		device.DeviceName,
		device.DisplayName,
		device.Enabled,
		LatestLogTime = FormatDateTime(device.LatestLogTime),
		LatestReportedAddresses = device.LatestReportedAddresses?.Select(address => address.ToString()).ToArray() ?? [],
		LatestReporterRemoteAddress = device.LatestReporterRemoteAddress?.ToString(),
		device.RecentLogCount
	};

	private static object MapOnlineLogSummary(OnlineLogSummary log) => new {
		log.OnlineLogId,
		log.DeviceId,
		log.DeviceName,
		log.DeviceDisplayName,
		LogTime = FormatDateTime(log.LogTime),
		ReportedAddresses = log.ReportedAddresses.Select(address => address.ToString()).ToArray(),
		ReporterRemoteAddress = log.ReporterRemoteAddress?.ToString(),
		log.SubmittedByUserId,
		log.SubmittedByUserName,
		log.SubmittedByUserDisplayName,
		log.Message
	};

	private static object MapOnlineLogDetails(OnlineLogDetails log) => new {
		log.OnlineLogId,
		log.DeviceId,
		log.DeviceName,
		log.DeviceDisplayName,
		LogTime = FormatDateTime(log.LogTime),
		ReportedAddresses = log.ReportedAddresses.Select(address => address.ToString()).ToArray(),
		ReporterRemoteAddress = log.ReporterRemoteAddress?.ToString(),
		log.SubmittedByUserId,
		log.SubmittedByUserName,
		log.SubmittedByUserDisplayName,
		log.Message
	};

	private static string? FormatDateTime(DateTime? value) =>
		value?.ToString("O", CultureInfo.InvariantCulture);

	private static string? GetString(JsonElement arguments, string propertyName) =>
		arguments.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static string? GetRequiredString(JsonElement arguments, string propertyName) {
		var value = GetString(arguments, propertyName);
		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	private static long? GetLong(JsonElement arguments, string propertyName) {
		// 模型可能把数字参数生成为字符串，这里同时兼容 number 和 string
		if (!arguments.TryGetProperty(propertyName, out var value)) {
			return null;
		}
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) {
			return number;
		}
		if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), CultureInfo.InvariantCulture, out number)) {
			return number;
		}
		return null;
	}

	private static int GetBoundedInt(JsonElement arguments, string propertyName, int defaultValue, int minimum, int maximum) {
		// take 等数量参数必须做边界收敛，避免一次工具调用返回过多数据
		if (!arguments.TryGetProperty(propertyName, out var value)) {
			return defaultValue;
		}

		int? parsedValue = null;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) {
			parsedValue = number;
		} else if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), CultureInfo.InvariantCulture, out number)) {
			parsedValue = number;
		}

		return Math.Clamp(parsedValue ?? defaultValue, minimum, maximum);
	}

	private static string CreateArgumentsSummary(string argumentsJson) {
		// 参数摘要只用于前端展示，不参与后续模型上下文
		if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson == "{}") {
			return "无参数";
		}

		try {
			using var document = JsonDocument.Parse(argumentsJson);
			return string.Join(", ", document.RootElement.EnumerateObject()
				.Select(property => $"{property.Name}={GetJsonElementSummary(property.Value)}"));
		} catch (JsonException) {
			return "参数解析失败";
		}
	}

	private static string GetJsonElementSummary(JsonElement value) =>
		value.ValueKind switch {
			JsonValueKind.String => value.GetString() ?? string.Empty,
			JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
			JsonValueKind.Null => "null",
			_ => value.GetRawText()
		};

	private static string NormalizeErrorText(string responseText) {
		// 不同供应商的错误响应差异较大，优先读取常见 error.message，再回落到原始文本
		if (string.IsNullOrWhiteSpace(responseText)) {
			return "响应内容为空。";
		}

		try {
			var root = JsonNode.Parse(responseText) as JsonObject;
			var error = root?["error"] as JsonObject;
			return error?["message"]?.GetValue<string>()
				?? root?["message"]?.GetValue<string>()
				?? responseText;
		} catch (JsonException) {
			return responseText;
		}
	}

	private static JsonObject CloneObject(JsonObject source) =>
		JsonNode.Parse(source.ToJsonString()) as JsonObject
		?? throw new InvalidOperationException("无法复制 JSON 对象。");

	private static JsonArray CloneArray(JsonArray source) =>
		JsonNode.Parse(source.ToJsonString()) as JsonArray
		?? throw new InvalidOperationException("无法复制 JSON 数组。");

	private sealed record AgentToolResult(
		bool Success,
		string Content,
		string Summary
	);
}

/// <summary>
/// 表示 Agent 对话处理过程中出现的业务失败。
/// </summary>
public sealed class AgentChatException(string message) : Exception(message);