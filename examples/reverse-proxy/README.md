# ============================================================================
# Whalefleet —— 反向代理示例
# ============================================================================
#
# 镜像内**只监听明文 HTTP**（`:8080`）。生产部署必须由前端代理终止 TLS——
# 浏览器不会把 Bearer Token 挂在明文连接上。
#
# 本目录给出两份可直接改用的配置：
#
#   nginx/whalefleet.conf                  + nginx/whalefleet-proxy-params.inc
#   traefik/whalefleet.labels.yml
#
# ## 三件必须配对做的事，缺一即出问题
#
#   1. 代理传 X-Forwarded-Proto / X-Forwarded-For
#   2. 应用侧声明**可信代理**：`ReverseProxy__Enabled=true`
#      + `ReverseProxy__KnownProxies`（精确地址）或 `ReverseProxy__KnownNetworks`（CIDR）
#   3. 应用端口只对反代开放（compose 的 `ports` 绑到反代所在 IP，或由防火墙拦）
#
# ### 第 2 步为什么不是「可选的安全加固」
#
# 应用**默认不采信** `X-Forwarded-For`（`ReverseProxy:Enabled=false`）——
# 该头可被客户端任意伪造，无条件采信等于让攻击者自己选择登录限流的键，
# 每次换一个伪造 IP 就能无限重试。
#
# 但只做第 1 步会有另一个症状：**所有用户共用一个限流桶**。
# 应用看到的 TCP 对端永远是反代自己的地址，于是 5 次失败后全体用户在 1 分钟内
# 都登不进去；日志里「限流生效了」看起来完全正常，根本看不出问题出在 IP 上。
#
# 因此可信代理白名单是**启用反代后的必填项**，而不是锦上添花。
# 启动时会解析失败并拒绝启动（写错的地址不会被静默忽略）；
# 但**不要填 `0.0.0.0/0`** —— 那等于采信任何人的伪造头。
#
# ## 验证清单
#
#   1. `curl -I https://你的域名/healthz` 返回 200
#   2. 用手机流量（换 IP）登录一次，日志里能看到那个真实 IP，而不是反代的地址
#   3. 故意连错密码 5 次 → 立刻再从**另一个 IP** 登录，应当**不受影响**
#      （若一起被限，说明白名单没生效，回到第 2 步）
#   4. `docker logs <应用容器> 2>&1 | grep -i limiter` 观察限流命中
#
# ## 两份配置的取舍
#
# | 场景 | 选 nginx | 选 Traefik |
# |------|:---------:|:----------:|
# | 只需要 Whalefleet 一个服务 | ✅ | 可以 |
# | 与其它容器服务共存，不想改配置 | 一般 | ✅ |
# | 要精细控制超时 / 限流 / 缓存 | ✅ | 受限（限流需付费中间件） |
# | 证书自动续期 | 需另装 certbot | ✅ 内置 ACME |
#
# ## 认证与会话相关的额外注意
#
# 反代必须**保持 host 语义**：应用按 `Host` 生成链接，而
# 登录后前端把 access token 存在内存里、refresh token 存在 cookie 之外的本地存储，
# 因此不需要 `proxy_cookie_domain` 一类改写。只要 `X-Forwarded-Proto` 传对即可。
#
# WebSocket 与 SignalR（0.7.0 的实时通道）需要 `Upgrade` / `Connection` 头成对透传，
# nginx 版已在 `whalefleet-proxy-params.inc` 里处理。Traefik 自动处理这两条。
