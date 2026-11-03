# VRC-00A 基线与提取清单

日期：2026-11-03。子批次：VRC-00A。责任：guidebook_audit代理智能体完成源只读基线复核、逐文件提取/依赖表，以及00C验证矩阵与原创来源设计审核；协调者负责目标Git/Unity现场、版本包锁、总summary和批次门槛状态。

状态：本日志所列文档/源审查交付已写入并完成静态核对；VRC-00A整批状态由协调者汇总，业务功能及25子批/21用例族运行结果在本代理矩阵中全部NotRun。本代理没有运行Unity、编译、EditMode/PlayMode、Player、模型导入导出或互操作。

## 1. 进入条件、输入与保护范围

已全文读取最新两份规划（主规划459行，分批指引528行；长输出截断区段已补读）。Windows独立程序、UI Toolkit、自研FBX/GLB/OBJ导入和FBX导出为既定需求；Apple营销样式按用户已确认的桌面编辑工具适配。单层/网格/四向/预算等仍按主规划标注为起始工程方案。

| 当前规划输入 | 字节 | SHA256 |
| --- | ---: | --- |
| [visual-room-composer实施规划.md](<D:/DevOpus/visual-room-composer/Assets/Docs/visual-room-composer实施规划.md>) | 66720 | `7ea27366b6865ea71a00adadcf92538f5b9988acfffe485c5d9ab669aeee30a1` |
| [visual-room-composer分批部署指引.md](<D:/DevOpus/visual-room-composer/Assets/Docs/visual-room-composer分批部署指引.md>) | 44072 | `bf5a63afba697b3229f8f123fa59857f8822580337acd3576675cac67dd2b814` |

协调者现场基线：[baseline.json](<D:/DevOpus/visual-room-composer/Validation/VRC-00/20261103-122155/baseline.json>)，capturedAt=`2026-11-03T12:21:55.4573136+08:00`，Git HEAD=`ee9b74354a6da98499ed5981114ed586322db129`，记录57个目标tracked文件和41个Guidebook正文/配置文件。目标继承变更、Unity端口/场景/运行状态由该基线及协调者现场证据管理；本代理未重跑Git或Unity命令、未改基线。

本代理仅写三个授权文件：`Assets/Docs/batch_logs/VRC-00A-基线与提取清单.md`、`Assets/Docs/contracts/source-extraction-map.md`、`Assets/Docs/contracts/validation-matrix.md`。没有修改两份上游规划、其他代理合同/fixture、目标包/版本/代码/场景，亦未执行任何SCM写入或签入。源项目与资产目录严格只读。

## 2. 阅读差异与当前源结论

1. 主规划附录A/B共79条文件摘要，当前SHA256前12位全部匹配，无缺失。Guidebook当前41个正文/配置文件（38 Markdown、1 JSON、1 CSS、1 xlsx）与附录集合完全一致，无新增/删除；`.meta`不作正文计数。
2. 与上轮完整hash清单比对：本代理此前全文阅读的34个文本文件、源码审计代理此前28个文件均完整SHA256匹配。与协调者本轮baseline中的41个Guidebook文件也完整SHA256匹配。因此本轮没有旧规划变更需要补读；不是重新把全仓源代码宣称全文审计。
3. 本轮补读`AuthoringServices.cs`、`IProfileAssetService.cs`、`IProfileRepositoryCatalog.cs`、`ProfileMeshRequests.cs`、`IMeshTile.cs`全文，共304行，另全文读取四份SDD asmdef；其余新纳入源表文件按路径/using/类型/方法及相关调用检索核对。迁入或移植测试之前仍须全文阅读被标记“检索”的文件。
4. 本轮 source-extraction-map 记录154个实际源文件完整SHA256；写完后再次比对，154项均一致。结论限于这些记录文件及前述文档，未声称对两个旧目录全部资产做前后逐字节审计。
5. 继续提取当前SddShellView/CDT/Profile路径；旧DesignerRuntimeView、VGE、库存/采购/角色/官方内容发布剔除。旧节点/地图/包导出不是FBX writer；新writer仍为新增核心功能。
6. 新补充的依赖边界：旧`Platform/AuthoringFileDialogs.cs`直接using `TriLibCore.SFB`，不能只改导入服务便宣称去TriLib；`AuthoringDelta/History`实际依赖Newtonsoft字段树/压缩，需显式替换/适配；旧MeshAssets合同持有GameObject/Bounds，需拆CPU IR与Presentation租约；Cdt/Profile/Shell存在多个partial文件，提取必须追踪共同字段/事件，不能只拿主类文件。

全量阅读是上轮团队分工结果。本代理此前独立完成29份Guidebook Markdown+JSON/CSS、相邻资产3份Markdown，另完整读取工作簿4表458个非空单元格、无公式；主控/源码代理完成其余9份Guidebook Markdown。旧xlsx仍仅为SKU配置输入，不成为房间构件合同。

## 3. 本批交付与静态核对

| 交付 | 实际内容 | 当前证据边界 |
| --- | --- | --- |
| [source-extraction-map.md](../contracts/source-extraction-map.md) | 154个实际文件逐行处置、目标责任、首次批次、最小依赖闭包、完整hash和阅读状态；程序集方向及禁止生产依赖；素材来源策略 | 文档/源码研究，未迁入代码、未编译 |
| [validation-matrix.md](../contracts/validation-matrix.md) | 25子批次门槛、21用例族稳定子ID、证据层/预期、原创fixture审核、待补样例和隔离故障设计 | 所有运行结果NotRun；fixture生成不冒充Reader/Player通过 |
| 本日志 | 规划输入、来源差异、源版本/包锁和完整文档hash清单 | 源只读复核；现场状态由协调者汇总 |

静态核对实际执行：文件枚举与SHA256比较；上游文档全量读取；源引用/类型索引；新增接口全文阅读；原创Blender生成器及expected全文审核；三份授权产物存在、表格ID覆盖和引用文件可达核对。没有把这些检查命名为产品自动测试。

## 4. 源版本与包锁

| 源文件（相对ShoppingDash根） | 字节 | SHA256 | 处理 |
| --- | ---: | --- | --- |
| `ProjectSettings/ProjectVersion.txt` | 85 | `7a43a06dfe51c4e3933c5a7848320925ce96e667f8c7736557d13a02007e9860` | 只读记录；不复制到目标、不升级源 |
| `Packages/manifest.json` | 3230 | `1d3352700a2593dcabbaa2483c9350bad1ce4f6b0b448899aa23b3c05b92d77a` | 只读记录；不复制到目标、不升级源 |
| `Packages/packages-lock.json` | 23189 | `c2d4c74cd5587652e5f39aa3c0082aa2b79cc9120443a220d905f8f3884d0925` | 只读记录；不复制到目标、不升级源 |

旧工程继续为6000.3.24f1；目标7000.0.0a12及manifest/lock的实际现场锁定由协调者记录。本代理没有安装或移除包。

## 5. 当前文档完整hash清单

路径相对对应根；完整hash用于后续差异发现，阅读分工仍以主规划附录A为准。

| 根 / 文件 | 字节 | SHA256 |
| --- | ---: | --- |
| `Guidebook/docs-v1-preview/baseline/base_guideline.md` | 3500 | `c6f403c81c0b545983e5ed6c35ccdaf5577cc3c6e753d25bd0e486c6bbe0a37b` |
| `Guidebook/docs-v1-preview/baseline/内测关卡设计器使用说明.md` | 3789 | `c17076762a3e9e887b2e87ae5ee7a986ca88f4d2fc47dd3fe3352fc82cf38146` |
| `Guidebook/docs-v1-preview/baseline/开发路线.md` | 74790 | `369c603e5e490a51c2fc7c9bc08f3cee089f2886fdb5bea84dab1e2cf58e75ce` |
| `Guidebook/docs-v1-preview/baseline/程序集与源码文件参考.md` | 89330 | `e18ede844ced3f9a2f9a897b6026b1eceb7ed7fe5803524683319a80a09c1c89` |
| `Guidebook/docs-v1-preview/baseline/自建服务端规划（未启用）.md` | 13260 | `8895ce15a28b32ebfa2284a5bd284ea6be09524116c2d98dfaf6ff989b1b710a` |
| `Guidebook/docs-v1-preview/baseline/首版临时决断记录.md` | 5290 | `752cb4422b73cf33870a3e3d75eda12d6c121638f00718b907c2625df73e4aef` |
| `Guidebook/docs-v1-preview/baseline/首版部署记录.md` | 11011 | `f22009d8c5567089920a0238078959ba0a1bc0ac38f60104622aad58f5c5fa69` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B00-规划与需求核对.md` | 3589 | `5f3d638ef4637ca12493e002b4e3f07c4f121ba2bdd44ef75f1a0432c9e0b92a` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B01-技术探针与基础适配.md` | 10985 | `120b17a4003ce4998bfd1cf1f4e24a143e8e150a6b31cb6154dc3200b161be4f` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B02-作者数据与资源服务.md` | 4384 | `581757485e6654f0340d1ffc9434e71a7385651f06050b43963f91f1173b0990` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B03-Shell与增量历史.md` | 4731 | `eb6737899a9e45ac45ad98edbcb1c93dd44ff6c2bae9436ba655e274242628a4` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B04-VGE与交通图.md` | 6104 | `69835d8c55c0a425a5742f581ad3a488fc3f119f4ade86a5a00b7c31933f19ba` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B05-Profile与CDT.md` | 8259 | `7b5e3fcdf239d6c189d4e2ccbabec9546a006252a90a9c05a6ca36838a6b25d6` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B06-官方预设集与资产制作.md` | 7390 | `506e86adf53fa3f6fea530ed37ef8513b05216a2dd407228f7c03a0a80551abb` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B07-游戏接入与断代.md` | 9836 | `12aae5941680a6c76d10f79a6635d8b8562a2c887531711bd8d605fc4fefda1a` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B08-官方关卡与玩家流程.md` | 9278 | `272230fe3e5498e1fee7384a64b764dadfd9a6bb8ea30f8a963731ee128c6cfe` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B09-Windows候选与验收.md` | 16373 | `1bf986f6bc645bf75822d4c4e8de9197eb6ffd3dafbb117b7dedb957cf07eee0` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B10-交互与编辑体验修订.md` | 8696 | `d9fc0c6e47068fb71b5e626f64fed4b7f4cb7131f3273b9f2e59100939fc543e` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B11-角色与编辑体验.md` | 7274 | `e07d16c6c301b16eaf55b231677c19d2e585ac0b3cd88af790955ff3200f5541` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B12-本地加载优化.md` | 5674 | `1c533e83e40fa90edff2aee5fd125ca7118e6ccc4fc1e0afcfaf340c31fd9624` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B13-角色服装与操作优化.md` | 4890 | `06d37f37d5bea8e32f4235c54a18cb1b556f31b659453bd4a8b61e9d672ba7f7` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B14-安卓大关卡与角色物理.md` | 5381 | `4e94e26b02dc95d1cbf26ec1aa0f98fdfd4a113a4067f92681352489ecfa5598` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B15-安卓关卡进入修复.md` | 7458 | `ea64c5cf039a1c7ac81e2360556c3b02d32c1021e587a1ac20c9936b8c62d48c` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B16-角色自适应界面与内容扩建.md` | 25611 | `601dbcce4ca136c3c9e9bc050ea8b9436f227f8987e6e04c5eff2183a8c89de3` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B17-编辑笔刷库存陈列与点地交互.md` | 11638 | `f8cab3218f02b8290e926dfb71a5dd7cccf5b9c64d17f62c8414c698f992ebe8` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B18-采购进度与Inori点地运动.md` | 18364 | `1bca0e66e4d1605f47e806a610865a4e3a494033054fada6714d0c7bd209286b` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B19-画面抗锯齿与循环动画.md` | 2029 | `20a5c77154c6f82bc4d24de1d8595fac091627469eee3bbf66b6fb3f8d555675` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B20-章节导航与原版折线选关.md` | 2513 | `d906e9376f28f1b1ab625aa1c97abc9053405ffd96a008790ee1c9fd72409221` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B21-纹理压缩与资产分包.md` | 1754 | `70592f259e6005ea1f392de302481a4d451424f20d96d73392cb41cc27807c7d` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B22-原创暖色街景.md` | 1285 | `ff12a2a8d20d3a3f37ac64ed70fac9d540ea077a7bbba8438986be77735d2344` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B23-暖城十二关与多房间验证.md` | 3135 | `cca5151c819c53ccb427cb408ef8f00b458ef474ee6cc92f2a4d5dbeda3cabc3` |
| `Guidebook/docs-v1-preview/batch_logs/SDD-B24-联合回归与双平台构建.md` | 8073 | `cdaa05013dc1ce7e934a3e2339614ec2823bfedbc1047a8892bddaa4ce21dfaa` |
| `Guidebook/docs-v1-preview/designer/DESIGN.md` | 19799 | `582f4cb82fc2df321fd83b4fad33bc7ca9a96a369241d34309dd309b8c7c9f42` |
| `Guidebook/docs-v1-preview/designer/tokens.json` | 9301 | `c9c16549dc7e714dfe12369de90b081286f5d0482ac206854d12689b76e4650d` |
| `Guidebook/docs-v1-preview/designer/variables.css` | 1896 | `b76478177546466484d39ed5ca130758e467ba83c4e004b88ce40d47b76e44f8` |
| `Guidebook/docs-v1-preview/SDD使用说明.md` | 14745 | `bc92ab6d8c6ed5f993c03b4d9bd265edf5173c88e89370f8174f32bf08b83fa7` |
| `Guidebook/docs-v1-preview/SDD文档阅读与现状核对.md` | 6850 | `43c729378083b38e56628a8ccc1871f98292582e6c97cdcbb5af3995952ae305` |
| `Guidebook/docs-v1-preview/SDD重构规划.md` | 87982 | `bd246b6b8a42e2a7ca2a0ebc02da85094ce64a751bda66795a1bc7ffd65c0f78` |
| `Guidebook/docs-v1-preview/xlsx/极限购物_物品配置.xlsx` | 24602 | `62363b137a046266bae8fdae233533ec8356c6019c125ca5da1ffa0a50484325` |
| `Guidebook/docs-v1-preview/体验修订与角色动效规划.md` | 13432 | `cc9029425adcd4fe1e565e2eea2a4fe6a626ccb323e94bc827c1a553575b4edd` |
| `Guidebook/docs-v1-preview/大规模章节与画面修订规划.md` | 6479 | `1b6e03ac555170ff4f125a0df998fe73e574ee100942837fe478f4e10d5be4ac` |
| `AssetsDocs/ShoppingDash[极限购物]UI页面设计.md` | 15470 | `7aa289f6cb69ce3179be1be05941f384d1f5f2d6a992adbd3e25bea71935a29a` |
| `AssetsDocs/ShoppingDash[极限购物]游戏玩法.md` | 23693 | `60fa2c13060ffb31b51839372d6f5e6f60cffaa74eea6499adcccb2baf7ba3f2` |
| `AssetsDocs/界面图片需求清单.md` | 49311 | `6bf4b2bdcb3cb743704630eb7afd6136ad07ac2f2ecdf871c349693c82821321` |
| `TargetDocs/apple-design-docs.md` | 25526 | `4516ed14688a8b217194c1e17f931adc3b535ad731a1b1cc590354f2715b7793` |

## 6. 交接、未完成与下一步

- 00B代理按提取表的中立合同落RoomDocument/Library/ModelScene、错误和所有权；不得把旧namespace或游戏字段为省事重新带入。01B/01C探针改变共同合同时，协调者同步所有消费者。
- 00C格式代理维护真实fixture文件/索引/expected/hash与格式依据；本代理只审核已存在原创生成器及预期。矩阵中带洞墙、凹面、完整OBJ/GLB/版本/变换变体仍需逐项真实生成并执行，不因为有一个Blender正例就全部关闭。
- 01A由协调者检查目标实例/后端/API/原生文件框；01C须取得Player与两种独立消费者证据后才通过。当前未完成的运行门槛由summary列NotRun/待验证，不由本日志填通过数。
- 后续迁入前比对154文件hash；变更文件补读后更新基线，保留来源。任何未确认的商业素材保持源端，原创样例可继续开发，不以资产权利未知阻断通用格式实现。

现场恢复：本代理未进入PlayMode、未启动验证进程、未切换/保存用户场景、未移动/删除源或用户文件，无本代理Unity现场需要恢复。临时读取报告只复用既有Temp文件，未新写临时脚本或改记忆。最终版本控制清单及是否签入继续由用户决定。
