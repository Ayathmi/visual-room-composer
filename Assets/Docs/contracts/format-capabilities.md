# 格式能力合同：00C / 01C 可行性探针

版本：2026-11-03。此文件细化既有实施规划与分批部署指引，不将探针子集解释为 03、05 或最终产品完成。最终仍须交付运行时 FBX / GLB / OBJ 导入和 FBX 导出。当前不依赖 TriLib、Autodesk FBX SDK、UnityEditor、外部进程或第三方生产库；Blender 与 Python 仅用于项目外测试夹具生成及独立消费。未复制 TriLib 源码；此前审阅过 TriLib 的事实不构成 clean-room 声明。

## 当前可执行入口

程序集 `VisualRoomComposer.Formats` 仅引用 `VisualRoomComposer.Core`，`noEngineReferences=true`。

- `FbxModelReader.Read(path, FbxReadOptions)` 返回 canonical 米制 Y-up 左手 `ModelScene`。
- `FbxBinaryWriter.Write(scene, path, FbxWriteOptions)` 返回输出路径、纹理路径及损失警告；输入必须已经 canonical，目标目录必须存在且为空。
- `FbxProbe.Run(inputDirectory, outputDirectory, evidenceEnvironment)` 读取输入目录第一层全部 `.fbx`，每个输入建立独立输出子目录；返回可由 Unity JsonUtility 序列化的字段 DTO。`ProducedRequiresIndependentValidation` 仅表示读写完成，绝不等于消费者或 Player 验收通过。
- `FbxGuardProbe.Run(validInputPath, negativeDirectory, newScratchDirectory)` 执行 15 个确定性负例/预算/预取消/既有文件保留检查，scratch 目录必须不存在。

## 01C 支持矩阵

| 能力 | 当前实现 | 当前证据/边界 |
| --- | --- | --- |
| FBX 7400 Binary | 有界读取 | 原创 Blender 5.2.2 LTS 导出；实际离线读取 |
| FBX 7500 Binary | 64 位 record header / 25 字节终止记录读取 | 独立测试编码器转换；zlib 数组；不是第二款 DCC 导出证据 |
| FBX 7400 / 7500 ASCII | 注释、字符串、标量、数组、嵌套节点 | 独立编码器保留原始对象树；实际离线读取 |
| Binary 数组 | raw / zlib，数组长度与 Adler32 检查 | 有效压缩数组已走通；完整恶意压缩矩阵待 03 |
| 对象连接 | ID 唯一性、OO/OP、Model 父子、Geometry/Material/Texture 绑定 | 三节点、两个非对称 mesh、两个材质槽及纹理 |
| 静态三角 mesh | 控制点、统一角点属性索引、法线、UV0、按面材质 | 每面拓扑、槽绑定、每角 UV、面朝向经独立 Blender 检查 |
| Layer 映射 | ByVertice/ByVertex/ByControlPoint、ByPolygonVertex、ByPolygon、AllSame；Direct / IndexToDirect / Index | 不是每个组合均已金样验证；材质仅 AllSame/ByPolygon |
| 层级变换 | XYZ Euler；平移/旋转/比例；子节点 `InheritType=1` RSrs | 原创父级非均匀比例、旋转子级造成的仿射/剪切、镜像已验证世界几何 |
| Pivot / Pre / Post / Geometric | 明确 FBX 局部乘积、PostRotation 逆矩阵、Geometric 仅作用于本 mesh | 非零 rotation/scaling pivot、offset、pre/post、geometric translation/scale 独立金样已验证；geometric rotation 与非均匀比例叠加仍有消费者差异待查 |
| 轴与单位 | 显式 GlobalSettings 三轴/sign + UnitScaleFactor/100；一次性 canonical 化 | 当前金样 FBX Y-up / RH / 米；其他轴、厘米/毫米组合待补 |
| 材质 | DiffuseColor × DiffuseFactor、Opacity/TransparencyFactor | Phong/specular/PBR 等未保真，明确诊断损失 |
| 纹理 | DiffuseColor OP；相对路径 PNG/JPEG；编码数据进入 IR | 原创 4×4 PNG 外链解析；不证明 JPEG 解码、显示或色彩管理 |
| FBX 7400 Binary 写出 | 正确绝对 offsets、typed properties、层、连接、终止记录、footer；外链相对纹理 | 独立 Blender 消费通过；Unity 原生导入 / Windows Player 由协调者另行执行 |
| Writer 层级 | 保留节点名称与父关系；所有 world affine 烘焙至几何；节点矩阵 identity | 明确丢失原节点原点/pivot/实例共享。不是最终 05 层级保真策略 |
| GLB 2.0 导入 | **NotImplemented** | 03 必需交付；未被取消或改为可选 |
| OBJ / MTL 导入 | **NotImplemented** | 03 必需交付；未被取消或改为可选 |

明确拒绝：其他 FBX 版本、非三角多边形、子级 InheritType 0/2、非 XYZ Euler、动态蒙皮/变形/动画/约束、非 Mesh/Null Model、每节点多 Geometry、额外 UV、作者颜色/切线/副切线层、非 Diffuse 贴图通道、分层贴图、纹理变换、内嵌 Video.Content、非 PNG/JPEG、绝对/URL/UNC/越界相对路径、依赖路径中的 reparse point。未识别的可选 FBX 元数据不是完整支持承诺。

输出材质损失：仅 diffuse、opacity、base-color 纹理；metallic、roughness、normal、emission、alpha-mode 语义、sampler 等不保真。writer 返回固定损失警告。后续产品 UI 必须展示按本次实际内容计算的损失清单，不能将当前固定探针提示视为最终导出预检。

## 内存、取消、文件边界

读取默认：源 256 MiB、累计受计费分配 512 MiB、单数组/属性 2,000,000 元素、100,000 节点、深度 128（可配置上限 256）、单字符串/raw 1 MiB、30 秒；持续检查 CancellationToken。语法读取先验证长度/父记录边界，zlib 禁止 preset dictionary，限定精确输出长度并核验 checksum。语义层缓存 layer 数组，避免 ASCII 每角重建完整属性数组。

这些值是探针保护参数与保守累计分配估计，**不是实测 peak working set/GPU 预算**。托管容器、Core 验证/复制、运行时实现差异仍须在 03 实测；缺少真实纹理尺寸/像素解码预算、超大资产主线程分帧、完整 fuzz 与中途取消负例。

Writer 在烘焙前按节点实例及 mesh/纹理估计工作分配，默认上限 512 MiB；编码与纹理输出总量默认 256 MiB。只向空的私有目录 CreateNew 写入；失败尝试删除本次已创建文件；从不覆盖既有文件。预取消、预算拒绝、既有文件保留已有离线检查。

**此 writer 不是最终原子发布事务**：写多个纹理与 FBX 过程中无跨文件原子性，清理失败可能留下私有残件；不得直接作为用户导出入口。05 必须实现完整 staging 集、取消/故障回滚与清单校验、同卷原子发布、提交点后的恢复规则与失败清理证据。目录权限竞态、磁盘满、杀进程与中途取消仍 NotRun。

## 原创来源与证据

`Tools/VRC/Fixtures/index.json` 固定实际夹具 ID、路径、SHA-256、生成器脚本 hash、命令、版本；包含四种编码变体、额外非零 pivot 样例及六个负例。基础四样例预期由 Blender 作者端直接计算；pivot 样例的测试编码器设置了显式 FBX 属性，由独立 Blender 读取生成预期；均未依赖本项目 writer 或 reader 的再次读取。原生 `.blend` 与原创纹理保留以便重现；文件字节中的 Blender 随机 ID / 时间戳不保证跨次生成一致，须以索引 hash 绑定本次证据。

最新版离线证据由 `Tools/VRC/Interop/OfflineProbe.csproj` 在 .NET 10 执行，输出 JSON 明确环境；`check_blender_outputs.py` 由独立后台 Blender 读取产品 writer 输出，比较原始作者端的世界顶点、三角拓扑、每面材质、每角 UV、面朝向、纹理尺寸与逻辑父级。不能将 reader → writer → 自家 reader 等价比较作为互操作验收。

截至本次记录：离线五个读取/写出探针成功；15 个 guards 成功；独立 Blender 五个输出通过，世界位置最大误差约 `3.04e-7 m`。Unity 原生消费者第一次得到空模型，经三轮结构隔离定位为缺少匹配的 FileId / CreationTime：只补空 block、Definitions、SceneInfo 或 Creator 无效。新输出 `offline-compatible-01c` 已补固定兼容三元组，并由协调者的 Unity7000 原生 ModelImporter 验证五个输出全部通过；同目录 `blender-consumer.json` / `unity-consumer.json` 是这次**离线 .NET 产品输出双消费者**证据，尚不证明 Windows Player 已执行这些路径。`offline-01` 保留最初属性类型标签问题导致的消费失败；`offline-04` 保留负例夹具依赖缺失与预期码修正前的失败记录。`offline-03` 起增加逐面材质/角 UV/法线朝向检查；`offline-pivot-01c` 保留额外 geometric rotation 组合的消费者差异。最新可复现输出目录和 Player/Unity 证据应由本批验收记录引用，不能凭此文档静态摘要覆盖新失败。

`Tools/VRC/Fixtures/BuildingBlocks/index.json` 另收录独立 Blender 原创窗洞墙、L 形凹多边形、共享 mesh/反转实例材质、镜像及中文/空格纹理路径，保留 FBX/OBJ/MTL/GLB 和作者端独立预期，供后续 03 使用；这些样本的产品导入验收仍 NotRun。完整单位/轴组合、复杂 FBX inheritance/多 UV 以及更多恶意输入仍须按矩阵补齐。

## 公开资料边界

本实现依赖的公开语义/结构参考：[Blender 公共二进制格式说明](https://code.blender.org/2013/08/fbx-binary-file-format-specification/)、[Autodesk 节点变换说明](https://help.autodesk.com/cloudhelp/2019/ENU/FBX-Developer-Help/nodes_and_scene_graph/fbx_nodes/computing_transformation_matrix.html)、[Autodesk FBX token 定义](https://help.autodesk.com/cloudhelp/2018/ENU/FBX-Developer-Help/cpp_ref/fbxfiletokens_8h_source.html)。这些资料并不构成完整、无歧义的 FBX 规范。Blender GPL 测试程序只在外部进程使用；不将其源码或二进制打包进产品。第三方游戏资产与 TriLib 的许可仍按 00A 清单逐项核验，本代码文件不替代法律结论。

兼容元数据依据：独立参考文件及三轮 Unity 原生消费隔离确认 `FileId`、`CreationTime` 与 footer 需要匹配；[Blender 官方编码器中的说明](https://github.com/blender/blender-addons/blob/main/io_scene_fbx/encode_bin.py)同样指出 timestamp 关联校验，并固定一组已知有效的值。当前自主 writer 使用同一已验证协议数据组，未复制该编码器的程序结构或执行算法，Creator 保持 `visual-room-composer independent static probe`。`1970-01-01 10:00:00:000` 是固定兼容元数据，**不是实际导出时间，也不是作者、版权或许可声明**；实际时间、内容 hash 和导出器版本应在 05 的 export-report 中记录。动态计算任意时间对应 FileId 的算法不在当前已实现能力中。
