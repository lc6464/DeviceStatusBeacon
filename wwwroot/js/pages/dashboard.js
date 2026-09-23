const activityTarget = document.getElementById("dashboard-activity");
const totalLogCountTarget = document.getElementById("dashboard-total-log-count");
const deviceNamePlaceholder = "DeviceNamePlaceholder";

// 仪表板页面是单实例结构，直接按约定的元素 ID 读取即可
if (activityTarget instanceof HTMLElement) {
    const activityUrl = activityTarget.dataset.activityUrl;
    const deviceDetailsUrlTemplate = activityTarget.dataset.deviceDetailsUrlTemplate;
    if (activityUrl) {
        void loadDashboardActivity(activityUrl, activityTarget, totalLogCountTarget, deviceDetailsUrlTemplate);
    }
}

// 加载仪表板最近活动数据，并在成功后刷新对应内容区域
async function loadDashboardActivity(activityUrl, target, totalLogCountTarget, deviceDetailsUrlTemplate) {
    try {
        const response = await fetch(activityUrl, {
            headers: {
                Accept: "application/json"
            },
            credentials: "same-origin"
        });

        if (!response.ok) {
            throw new Error(`Unexpected response: ${response.status}`);
        }

        const data = await response.json();
        renderDashboardActivity(target, data, totalLogCountTarget, deviceDetailsUrlTemplate);
    } catch {
        // 按需摘要与最近活动共享同一请求，失败时统一回落到占位值
        setMetricValue(totalLogCountTarget, "-");
        target.dataset.activityState = "error";
        target.replaceChildren(createEmptyState("最近活动加载失败，请稍后刷新页面重试。"));
    }
}

// 把最近活动响应数据渲染为设备活动列表
function renderDashboardActivity(target, data, totalLogCountTarget, deviceDetailsUrlTemplate) {
    // 对外部 JSON 做最小结构约束，避免异常响应直接破坏页面渲染，同时做空值回落
    const accessibleLogCount = Number.isInteger(data.accessibleLogCount) ? data.accessibleLogCount : "-";
    const recentDeviceActivities = Array.isArray(data.recentDeviceActivities)
        ? data.recentDeviceActivities
        : [];

    setMetricValue(totalLogCountTarget, accessibleLogCount);

    target.dataset.activityState = "ready";
    target.replaceChildren(renderRecentDeviceActivities(recentDeviceActivities, deviceDetailsUrlTemplate));
}

// 把延后加载的摘要指标同步到顶部概览卡片
function setMetricValue(target, value) {
    if (target instanceof HTMLElement) {
        target.textContent = String(value);
    }
}

// 渲染近期活跃设备列表
function renderRecentDeviceActivities(devices, deviceDetailsUrlTemplate) {
    if (devices.length === 0) {
        return createEmptyState("近期暂无设备活动。");
    }

    const list = createElement("div", { className: "dashboard-activity-list" });
    for (const device of devices) {
        const deviceName = device.deviceName ?? "";
        const displayName = device.displayName?.trim();
        const latestLogTime = formatLocalDateTime(device.latestLogTime);
        const enabledText = device.enabled ? "启用" : "停用";
        const enabledClass = device.enabled ? "status-pill--success" : "status-pill--muted";
        const latestReportedAddresses = getRecentReportedAddressSummary(device.latestReportedAddresses);
        const latestReporterRemoteAddress = device.latestReporterRemoteAddress || "未知";
        const recentLogCount = Number.isInteger(device.recentLogCount) ? device.recentLogCount : 0;
        const deviceDetailsUrl = createTemplateUrl(deviceDetailsUrlTemplate, deviceNamePlaceholder, deviceName);

        // 使用 textContent 路径组装纯文本字段，避免依赖模板拼接和 HTML 转义
        const article = createElement("article", {
            className: "dashboard-activity-item",
            children: [
                createElement("div", {
                    className: "dashboard-activity-item__main",
                    children: [
                        createElement("div", {
                            className: "dashboard-activity-item__identity",
                            children: [
                                createElement("span", {
                                    className: `status-pill ${enabledClass}`,
                                    text: enabledText
                                }),
                                createDeviceIdentity(deviceName, displayName, deviceDetailsUrl)
                            ]
                        }),
                        createElement("div", {
                            className: "dashboard-activity-item__summary",
                            children: [
                                createLabeledDetail("近期日志", recentLogCount),
                                createLabeledDetail("最近上报时间", latestLogTime)
                            ]
                        })
                    ]
                }),
                createElement("div", {
                    className: "dashboard-activity-item__details",
                    children: [
                        createLabeledDetail("最近地址", latestReportedAddresses, {
                            valueClassName: "network-address",
                            overflowText: "..."
                        }),
                        createLabeledDetail("来源地址", latestReporterRemoteAddress, {
                            valueClassName: "network-address"
                        })
                    ]
                })
            ]
        });

        list.append(article);
    }

    return list;
}

// 仪表板最近活动只展示前两个地址，保持和设备列表一致的摘要密度
function getRecentReportedAddressSummary(addresses) {
    if (!Array.isArray(addresses) || addresses.length === 0) {
        return {
            text: "暂无地址",
            hasOverflow: false
        };
    }

    return {
        text: addresses.slice(0, 2).join(", "),
        hasOverflow: addresses.length > 2
    };
}

// 将服务端返回的时间值格式化为本地可读时间文本
function formatLocalDateTime(value) {
    if (!value) {
        return "暂无日志";
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
        return "时间未知";
    }

    return new Intl.DateTimeFormat("zh-CN", {
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
        hour12: false
    }).format(date);
}

// 创建用于“标签 + 值”结构的详情行
function createLabeledDetail(label, value, { valueClassName, overflowText } = {}) {
    // 标签仍使用独立 span，方便复用现有 detail-label 样式
    const row = document.createElement("div");
    const valueText = typeof value === "object" && value !== null && "text" in value
        ? value.text
        : value;
    const hasOverflow = typeof value === "object" && value !== null && Boolean(value.hasOverflow);
    const valueElement = createElement("span", {
        className: valueClassName,
        text: valueText
    });

    if (hasOverflow && overflowText) {
        valueElement.append(
            " ",
            createElement("span", {
                className: "subtext",
                text: overflowText
            })
        );
    }

    row.append(
        createElement("span", {
            className: "detail-label",
            text: label
        }),
        valueElement
    );

    return row;
}

// 创建 Dashboard 和列表页共用的设备身份结构
function createDeviceIdentity(deviceName, displayName, deviceDetailsUrl) {
    return createElement("div", {
        className: "entity-identity",
        children: [
            displayName
                ? createElement("span", {
                    className: "entity-identity__display",
                    text: displayName
                })
                : null,
            deviceDetailsUrl
                ? createElement("a", {
                    className: "entity-identity__name entity-identity__link",
                    text: deviceName,
                    attributes: { href: deviceDetailsUrl }
                })
                : createElement("span", {
                    className: "entity-identity__name",
                    text: deviceName
                })
        ]
    });
}

// 使用 Razor 生成的路由模板构造详情地址，兼容应用部署在非根路径下的情况
function createTemplateUrl(template, placeholder, value) {
    if (!template || !value) {
        return null;
    }

    return template.replace(placeholder, encodeURIComponent(value));
}

// 创建统一的空状态占位块
function createEmptyState(message) {
    return createElement("div", {
        className: "empty-state",
        text: message
    });
}

// 创建带可选类名、文本和子节点的 HTML 元素
function createElement(tagName, { className, text, children = [], attributes = {} } = {}) {
    const element = document.createElement(tagName);

    if (className) {
        element.className = className;
    }

    if (text !== undefined) {
        // 统一走 textContent，把动态值限制为纯文本写入
        element.textContent = String(text);
    }

    for (const [name, value] of Object.entries(attributes)) {
        if (value !== undefined && value !== null) {
            element.setAttribute(name, String(value));
        }
    }

    for (const child of children) {
        if (child) {
            // 只接受已构造完成的节点，避免把原始字符串混入 DOM 构造路径
            element.append(child);
        }
    }

    return element;
}