# 发布为独立新项目

目标账号：37f。
仓库名称：PixivBatchBookmark-Unified。
描述：Pixiv 批量收藏 Windows 合一版，保留原 WPF 与 PySide6 两个版本，支持按钮双向切换。
公开范围：公开，与两个原项目一致。
默认分支：main。

上传仓库根目录的完整源码，包含 legacy/、resources/、tests/、构建脚本及 Windows workflow。
不上传本机 bin/、obj/、虚拟环境、登录配置和本地编译临时文件。
保留两个原仓库作为来源，不修改或删除它们。

源码提交后，Unified Windows build 将运行两个原项目的测试、实际切换检查及便携包构建。
Windows 实际 UI/EXE 验收以 workflow 的成功结果为准。
