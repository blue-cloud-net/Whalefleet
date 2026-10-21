# ============================================================================
# Whalefleet —— nginx 公共代理参数（被 whalefleet.conf 的 include 引用）
# ============================================================================
#
# ## 为什么拆成独立文件
#
# 四个 location（登录 / 其余 API / 前端 / WebSocket）的反代设置**必须逐字相同**。
# 复制四份看着省事，实际后果是：将来要加一个头就得改四处，漏掉的那一处
# 只在某个分支上出问题——而这类偏差（比如 WebSocket 那处漏了 Connection）
# 症状极其间接。
#
# 把共同部分抽出来，差别只剩 `proxy_pass` 与限流区名。
#
# ## 部署
#   cp whalefleet-proxy-params.inc /etc/nginx/conf.d/
#   （与 whalefleet.conf 放同一目录，include 用绝对路径才不会因工作目录不同而找不到）
# ============================================================================

proxy_http_version 1.1;

# ---------------------------------------------------------------------------
# 转发头 —— 与应用侧的 ReverseProxy__* 配置配套，缺任一半都会出问题
# ---------------------------------------------------------------------------

# 客户端真实 IP。应用默认**不采信**这个头（需 ReverseProxy__Enabled=true
# 且配了可信代理白名单），因为它可被伪造；配好白名单后它才生效。
#
# $proxy_add_x_forwarded_for 是**追加**而非覆盖：链路上有多层代理时
# 只有覆盖才能得到最接近客户端的那一跳。
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

# 后端据此判断原始请求是 https 还是 http。
# 配错的症状：应用生成的链接与重定向会退回 http://，
# 浏览器跟着跳一次 HTTPS → 页面能开，但地址栏永远带一跳 http。
proxy_set_header X-Forwarded-Proto $scheme;

# 宿主机名。缺它时应用无法拼出对外正确的绝对地址。
proxy_set_header Host $host;

# WebSocket / SignalR 升级（0.5.0 的实时通道依赖它）。
# 这两行不能省：HTTP/1.1 的 Upgrade 头一旦被丢掉就是**静默失败**，
# 症状是「订阅连上了但一条消息都收不到」。
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection $connection_upgrade;

# 代理超时。默认 60s 对容器操作偏短——拉镜像 / 删除大镜像可能更久，
# 而提前被断开的请求在后端仍会继续执行，前端只能看到「超时」而实际已生效。
proxy_connect_timeout 10s;
proxy_send_timeout    300s;
proxy_read_timeout    300s;

# 容器日志与镜像列表响应可以很大，默认 1m 会截断。
proxy_buffering off;
proxy_max_temp_file_size 0;

# 不把上游地址回传给客户端。
proxy_redirect off;
