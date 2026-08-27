# RuntimeBridgeProbe

`RuntimeBridgeProbe` 是注入链路的验证模块，不是 HSR 资源 Dumper。它只写入
一个 `bridge_probe` 事件，证明目标进程加载了 DLL 并能读取 `.hook` 会话环境。
它不会生成 Shader、Material 或资源路径数据。

使用 Visual Studio 2022 Build Tools 构建：

```powershell
cmake -S .\src\RuntimeBridgeProbe -B .\artifacts\probe-build -G "Visual Studio 17 2022" -A x64
cmake --build .\artifacts\probe-build --config Release
```

输出 DLL 位于：
`.\artifacts\probe-build\Release\my-hook-runtime-probe.dll`
