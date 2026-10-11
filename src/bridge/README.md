# NX .NET桥接

M2Bridge.cs及契约文件实现IPC、所有权、STA队列和操作计划；M2JsonlHelper.cs为JSONL客户端。M2Entry.cs接入NXOpen适配器。M2OpenModelAdapter.cs负责读写、验证和保存；M3NxFeatureFactory.cs创建原生草图与有限拉伸。

tools/build-m2-bridge.ps1构建workspace/build/current/M2中的四个生产文件。M2Startup.cs提供NX官方Startup入口；专用profile/startup只加载该shim，再从原址加载M2Bridge/M2Entry。它不接管或检查模型。运行及限制见[项目说明](../../README.md)。C#包围盒使用UFSession.Modl.AskBoundingBox(tag,double[])；圆弧半径和角度为double，不使用Python命名或隐式重载转换。
