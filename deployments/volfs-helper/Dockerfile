# 文件浏览辅助镜像（0.14.0）
#
# ## 这个镜像做什么
# Docker **只给容器**提供文件接口（`/containers/{id}/archive`），卷没有。
# 因此「浏览卷里的文件」只能把卷挂到一个容器里，再从那个容器读。
#
# 这个镜像就是那个容器用的：它**不需要真的跑起来**——
# 卷浏览全程只调用 `archive`（对 `created` 状态的容器同样可用），
# 容器的 `Cmd` 只是一个永远不会被执行到的占位符。
#
# ## 为什么用 scratch 而不是 busybox
# - **体积**：scratch + 一个空文件 = 约 5 kB；busybox 是 4 MB 以上，
#   而它在这里一行命令都不会执行。
# - **许可**：busybox 是 GPLv2，把它塞进发布物会给分发加上源码提供义务。
#   我们自己生成的内容没有这个问题。
# - **安全**：镜像里没有任何可执行文件，即使有人意外启动了它，
#   也没有 shell 可以敲。
#
# ⚠️ **不要给这个镜像加功能**（例如塞个 tar 进去「顺便解包」）：
# 一旦它需要执行，就必须有 libc 与 shell，上面三条优势会同时消失，
# 而且攻击面从一个「空壳」变成一个「能执行任意命令的容器」。

FROM scratch

# 占位文件：scratch 镜像不能为空（空层在某些构建后端上会被优化掉）。
# 路径刻意用 `/whalefleet-file-browser`，与应用里的 `Cmd` 逐字一致——
# 这样 `docker inspect` 时一眼能看出这个容器是干什么的。
COPY entrypoint /whalefleet-file-browser

# 挂载点：应用会把目标卷挂到这里（只读浏览用 :ro，写入时另起一个可写容器）。
VOLUME ["/whalefleet-volume"]

LABEL org.opencontainers.image.title="whalefleet-volfs-helper" \
      org.opencontainers.image.description="Volume file browsing helper for Whalefleet (never started, only used for the archive API)" \
      org.opencontainers.image.source="https://github.com/whalefleet/whalefleet"
