# VRC-00B 历史、导入、库与导出生命周期

版本：1.0-draft。日期：2026-11-03。配套[数据合同](00B-data-contracts-v1.md)。本文定义后续服务的行为合同；除Core不可变模型与CPU租约外，不能据本文表格宣称完整服务已经实现。

## 1. 独立编辑历史（VRC-02A/04B待实施）

工程与当前库草稿各持有独立historyId、stateId、dirty和savepoint。撤销/重做按活动工作区分派；文本输入内部撤销优先消费，不把工程历史用来撤销未提交文字。共享预算不合并两条时间线，历史面板必须标明目标。

初值为会话合计500步、256 MiB历史预算。事务先估算/保留额度，超过整条事务可用上限则在执行前拒绝；无变化事务不入历史、不改dirty、不消耗永久额度。前态不匹配或执行失败回滚整笔，不发布中途事件。清理较旧历史可释放其资源租约，但不能清理当前文档、当前草稿或导出快照仍固定的版本。

新命令截断当前文档的重做分支，不能截断另一工作区分支。dirty来自当前逻辑状态与保存点是否一致；成功工程保存只更新对应工程savepoint；库草稿保存只更新库草稿。导出、缓存、恢复副本写入、预览成功和后台导入完成均不能清工程dirty。恢复副本打开为新会话副本，不伪造完整历史恢复。

连续笔刷、拖拽或批量变换只在Commit提交一笔；Esc、失焦、切房间或切文档取消Preview/Dragging，恢复前态。后台历史跳转持有documentGeneration与起始revision，取消/过期结果只释放资源。

库发布产生不可变定义新版本，与库草稿历史分开；撤销草稿不删除已发布版本。升级工程引用是单独可撤销命令，记录旧/新固定三元组，并持有两边所需资源。其他工程不因该发布被自动升级。

## 2. 导入边界与状态（VRC-03A/03E待实施）

Reader只接触固定来源快照、允许的依赖根、选项、预算、取消令牌和实际进度，不创建Unity对象或写正式库。Reader返回源场景或明确规范化场景、诊断与依赖清单；统一服务验证其坐标状态，不能再猜测。Realizer只消费已验证Canonical IR，返回独立预览句柄，完整材质与纹理就绪后才能报告预览完成。

```text
Queued -> Probe -> Parse -> Normalize -> Validate -> DecodeTextures -> RealizePreview
RealizePreview -> AwaitingConfirmation -> PrepareLibrary -> CommitLibrary -> Committed
提交点前各状态 -> Cancelling -> Cancelled
提交点前各状态 -> Failed
```

每个作业有jobId、documentGeneration、起始revision、sourceSnapshotId、optionsDigest及唯一终态。阶段结束只代表该阶段完成，几何就绪不等于材质就绪或入库成功。AwaitingConfirmation只在隔离预览区存在；用户确认前不得登记正式库或修改工程。

取消是合作式停止：词法、三角化、解压、规范化、解码及Unity分帧循环定期检查同一取消信号和软超时。不能用Task.WhenAny返回超时后放任后台继续分配。关闭文档先撤销代次并取消作业，再解绑输入/事件，释放预览实例、解码资源、文件锁和租约。旧代次完成回调只能释放，不能写入新文档。

库提交有可记录的线性化点：提交前取消不发布；提交已生效则报告Committed并保留其记录，不能显示Cancelled而实际文件已成功。失败不登记半成品。原生文件框的用户取消返回Cancelled而非错误，线程/窗口所有权由平台实现验证；不得用UnityEditor文件框冒充Player支持。

待实施中立接口职责：IModelImportService负责上述作业；IModelReader只负责格式；IModelRealizer负责主线程实例；ILibraryTransaction负责完整集合发布。01A已在Core实现最小IFileDialogService：返回绝对路径表示Selected，null表示Cancelled，显式异常表示Failed；Windows实现在Infrastructure，正式UI负责显示状态与修复动作。实际01C格式探针入口可以更窄，不能以探针类型冒充生产服务。

## 3. 输入及预算

以下为可版本化起始配置，实际报告保存本次取值。Reader必须在分配前checked计算长度，不能等IR构造完成后才拒绝超大输入。

| 配置 | 初值 | 限制点 |
| --- | ---: | --- |
| 单源/单依赖 | 256 MiB | 文件快照与读缓冲前 |
| 解析缓冲总量 | 512 MiB | 所有解压/词法/临时几何驻留，不能每个数组分别独享 |
| 解码纹理总量 | 512 MiB | 预检像素/通道/层级后，计入实际驻留 |
| 单纹理边长 | 4096 | 解码前，包含宽高乘法溢出检查 |
| 唯一模型顶点 | 2,000,000 | 统一属性索引展开后也必须计数 |
| 三角形 | 4,000,000 | 凹面三角化输出也必须计数 |
| 网格 / 节点 / 深度 | 4096 / 65536 / 128 | 对象表、图求值及烘焙展开均先预估 |
| 导入并发 | 1 | 整个服务的候选驻留预算 |
| 软超时 | 30秒 | 合作式停止并保留阶段，不等于线程安全强杀 |

源依赖总字节、编码纹理驻留、属性/字符串/连接数、输出展开与GPU估算需要分别计量，不能简单把各上限相加作为最低内存承诺。01B ModelValidationLimits仅实现解析后节点/网格/顶点/三角形/深度/编码纹理检查；完整统一驻留记账与Reader分配前限制在03A及格式探针中实现。

来源默认禁止URL/UNC和路径逃逸；依赖必须位于已选择或明确加入的允许根内，符号链接解析后的真实路径也须验证。读取使用固定快照或防替换句柄，检查后源改变不能混入同一导入。复制后再次核对hash，Link重开时核对固定hash。可选缺失外观不能静默当完整成功；需要明确修复或具有显式损失声明的另行流程。

## 4. 错误合同

诊断至少包含format、stage、source、byteOffset或nodePath、stableCode、severity、message和suggestedAction。Core ModelDiagnostic目前将format归入调用服务上下文、节点位置用NodeId；Formats适配需保留原始定位，不只吐出异常堆栈。错误不导致部分正式提交。

| stableCode族 | 用户可采取的动作 / 服务后果 |
| --- | --- |
| UnsupportedFormat / UnsupportedVersion / UnsupportedFeature | 换到支持的版本或修正来源；若属承诺目标能力，该批仍未完成 |
| InvalidHeader / TruncatedData / InvalidLength / InvalidIndices | 源文件损坏或不合法，停止解析，不创建Unity对象 |
| DuplicateId / MissingReference / HierarchyCycle | 指出具体对象/连接；拒绝不完整场景 |
| NonFinite / InvalidTransform / DegenerateGeometry | 指出几何或节点；不替换成identity、不静默省略 |
| InvalidAttribute / InvalidNormal / InvalidTangent / InvalidMaterial / MissingAttribute | 指出属性或材质槽，修复来源后重试 |
| MissingDependency / PathOutsideAllowedRoots / SourceChanged / HashConflict | 提供依赖重定位或重新导入入口；不自动升级或放宽允许根 |
| BudgetExceeded / DeadlineExceeded | 说明受限阶段与测得值，停止工作并释放候选 |
| Cancelled / StaleGeneration | 受控终态，无失败恐吓文案，不改变已打开的新文档 |
| IoDenied / IoLocked / DiskFull / PublishConflict | 保留旧成功版本，在权限/锁/空间/版本冲突解决后重试 |
| MaterialConversionLoss / AttributeExportLoss / PreviewOnlyLight | 导出前可见警告与报告条目，不能静默宣传无损 |

拒绝恶意输入的测试成功只证明拒绝路径，不证明正常格式实现。未知required扩展、骨骼/形变、未实现继承等不得通过抛弃信息构造看似有效IR。

## 5. 库、保存与文件集事务（VRC-02C/05C待实施）

单文件保存：读取预期旧hash；写同目录独立临时文件；落盘并回读验证schema/内容；提交前复核旧hash；原子替换。失败保留旧文件，不更新savepoint。恢复写入另行副本，正常工程的dirty与保存点不受影响。

多文件库/导出：在目标父目录同卷建立独立版本目录，准备模型、纹理、报告和manifest全部文件，逐项验证及摘要，然后提交入口或发布版本目录。不能先覆盖FBX再写纹理。跨卷不假定rename原子；源文件、输入依赖、已有成功产物必须受到保护。

提交前取消不发布；提交后以已提交终态收尾。进程中断后核对入口hash、候选manifest及旧集合完整性：确认已提交则恢复已提交记录，未提交保留旧入口；无法判断或外部变化则停止覆盖并留证。不要因后来取消而删除已成功发布的新版本。原子性的具体文件系统实现及故障注入须在02C/05C证实，本合同不是已完成磁盘测试。

## 6. 导出快照与范围（VRC-05A待实施）

ExportSnapshot必须固定projectId、revision、stateId、文档hash、全部固定依赖、importOptions/importer版本、变换/烘焙规则版本、范围参数、选中ID和资源租约。制作快照时记录源数据版本；后续编辑允许继续，但不能读取后来变化的ModelScene或材质混入本次产物。关闭导出只释放自己的持有，不销毁文档或历史的资源。

| 范围 | 精确集合 |
| --- | --- |
| 全工程 | includeInExport=true的物理实例，按placementId去重；作者hiddenInEditor不排除 |
| 当前房间 | 主roomId匹配对象，加Room.sharedStructurePlacementIds，统一去重 |
| 选中组/实例 | 递归展开选中组，加入选中实例，宿主纳入时加入其附件；附件单独选中只输出自身并报告缺宿主上下文 |

所有范围都先按上表展开候选闭包，再统一应用includeInExport过滤与placementId去重；房间共享登记、选择组或宿主附件规则不能把作者明确排除的网格带回。预检逐项列出排除对象，必要的空父节点仍可保留名字；宿主被排除而附件保留时报告缺少宿主几何上下文。

保留必要空父节点与名字，但不因为父节点存在就带入未选兄弟网格。组没有额外变换，房间/组层级不能使世界位置改变。默认保留工程原点；任何将来重置原点选项均需显式保存并进入报告。共享墙不能输出两份重合几何。

缺网格、非法索引/矩阵、无法求值依赖必须阻断对应输出，不静默省略。作者拓扑诊断不等于导出错误，开放房间仍可导出。编辑相机、格线、gizmo、选择描边、碰撞辅助、未完成候选和UI均排除。纯灯光场景没有有效几何，不能伪报成功空文件。

ExportReport保存工程/快照revision和hash、范围、输出FBX版本/轴/单位、规则版本、对象/网格/三角形/材质数、依赖摘要、遗漏/转换清单及完整文件集摘要。FBX传统材质映射不承诺PBR像素一致；metallic/roughness/AO/顶点色/切线等未输出项目须逐项列明。FBX文件不等于可继续编辑的工程，成功导出绝不清dirty。
