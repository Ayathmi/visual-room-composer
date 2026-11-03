# VRC-00B 合同基线

日期：2026-11-03。状态：通过（设计交付）；不表示所有合同服务已经实现。

进入条件：00A现场基线、154文件提取表及用户已确认的Windows独立程序/Apple桌面适配。源文档未变，读取依据与文件摘要见00A日志。

实际交付：[数据合同](../contracts/00B-data-contracts-v1.md)、[生命周期与错误](../contracts/00B-lifecycle-and-errors-v1.md)、[完整格式目标表](../contracts/00B-format-capabilities-v1.md)。明确全局placements、房间/组无环、共享墙同一ID、附件闭包、隐藏与includeInExport分离；工程/库独立历史、共享预算、固定库版本、快照资源租约和事务提交点。

01根据探针冻结了Core中立IR、完整仿射、规范坐标/镜像/法线、CPU租约与最小IFileDialogService；Core/Formats无UnityEngine引用。RoomDocument/LibraryDraft、生产ImportJob、完整资源缓存、原子库/导出事务仍按后续批次实现，不以草案DTO或静态表格宣称产品具备这些能力。

验证为合同全文互审、程序集/禁止引用检查、公开API消费者编译与实际01探针对照；运行证据见`Validation/VRC-01ABC/20261103-probe-05/summary.json`，纯数据12项另列。没有把00B文档检查命名为自动产品测试。合同调整已同步平台接口所在层及现有返回路径/null/异常的三种终态；后续有影响的调整需同时更新消费者及验收。

本批修改范围为目标contracts、Runtime/Core及对应纯数据检查入口；源项目只读，没有SCM提交/签入。下一消费者为02A与03A：02A先建立独立文档/历史，03A基于既有IR建立完整作业/预算管线，不反向依赖UI。
