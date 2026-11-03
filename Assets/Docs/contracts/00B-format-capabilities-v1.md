# VRC-00B 格式能力与实现证据分离表

版本：1.0-draft。日期：2026-11-03。依据[主规划第6、7节](../visual-room-composer实施规划.md)。这是一份必须实现的能力清单，不是当前已经支持的宣传表。01C探针的具体实现和运行证据由其独立报告维护；只有绑定fixture、输入hash、后端及结果后才可把对应能力记为已验证。

能力记录键固定为format、version、encoding、feature、fixtureId、importerVersion、backend。每行分开保存implementationStatus与verificationStatus：前者可为NotImplemented、ProbeOnly、Implemented；后者为NotRun、Passed、Failed、Blocked、NotApplicable。独立纯数据成功不自动升级Player/双消费者状态；生产完结要求全部必需目标经过03及07门槛。

| 能力ID | 已采纳的必需目标 | 当前生产状态 | 失败/拒绝及必需样例 |
| --- | --- | --- | --- |
| OBJ-INDEX | v/vt/vn/f四种face写法、正负索引、独立属性索引展开 | 未实现、未验证 | 0索引、越界、属性缺口定位；负索引、UV接缝、法线硬边样例 |
| OBJ-TOPOLOGY | 三角/四边/简单凹面，对象/组、平滑组 | 未实现、未验证 | 自交/退化面、曲线/NURBS拒绝；凹面不能全用fan |
| OBJ-MATERIAL | 多MTL、相对PNG/JPEG、Kd/Ks/Ns/d/Tr及声明支持的map参数 | 未实现、未验证 | 缺依赖、中文/空格路径、材质槽、透明及不支持参数诊断 |
| OBJ-COORDINATES | 用户显式单位/上轴，写入固定导入选项 | 未实现、未验证 | 不靠后缀猜单位；非对称标尺、多对象和已烘焙源几何样例；OBJ不承诺节点层级transform |
| GLB-CONTAINER | glTF2 header/chunk、4字节对齐、JSON/BIN | 未实现、未验证 | 截断、错长度、chunk越界、伪后缀拒绝；独立.gltf不替代GLB |
| GLB-ACCESSOR | bufferView/accessor offset/stride/normalized/sparse | 未实现、未验证 | checked范围、稀疏索引和默认零数据；不同componentType样例 |
| GLB-GEOMETRY | 索引/无索引triangle/strip/fan，UV0/UV1、法线、切线、颜色 | 未实现、未验证 | 点线/skin/morph/动画拒绝，退化拆分明示；不是仅一种三角路径 |
| GLB-TRANSFORM | 节点TRS/matrix、完整静态层级、反射 | 未实现、未验证 | 循环、非有限数、奇异拒绝；nested非均匀缩放与shear样例 |
| GLB-MATERIAL | metallic-roughness、normal/AO/emissive、alpha、doubleSided、PNG/JPEG | 未实现、未验证 | 通道/色彩空间/UV/采样与纹理变换明确；外部依赖也走resolver |
| GLB-EXTENSIONS | 未支持required扩展拒绝；optional可忽略但报告损失 | 未实现、未验证 | Draco、meshopt、KTX2未实现时清楚拒绝；不因缩略图没用到便忽略required |
| FBX-CONTAINER | ASCII/Binary 7400与7500，压缩/未压缩数组 | 01C探针实施中；生产未完成、未验证 | record位宽/边界/结束记录、checked长度、精确解压大小、深度、截断与错误encoding |
| FBX-GRAPH | Objects/Connections、实例/层级、默认/模板属性 | 01C探针实施中；生产未完成、未验证 | 坏引用、重复ID、循环、连接类别；共享网格不同材质槽 |
| FBX-GEOMETRY | polygon结束编码、控制点与polygon-vertex展开、layer mapping/reference | 01C探针实施中；生产未完成、未验证 | ByVertice/ByVertex、ByPolygonVertex/ByPolygon/AllSame、Direct/IndexToDirect；Index别名需明确实现或拒绝；凹面 |
| FBX-TRANSFORM | GlobalSettings轴/单位、rotation order、pre/post、pivot/offset、geometric transform、InheritType | 01C探针实施中；生产未完成、未验证 | 每种声明支持的继承有非均匀缩放样例；父geometric不传子；未求值形式拒绝并保持目标未完成 |
| FBX-MATERIAL | 材质槽、UV/法线/颜色、外部及声明受支持的嵌入纹理 | 01C探针实施中；生产未完成、未验证 | 缺依赖/程序或分层材质定位；传统材质转PBR列出变化 |
| FBX-UNSUPPORTED | 6.x、其他未验证7.x、骨骼/动画、NURBS、约束等不默认支持 | 约束已规定，拒绝路径未验证 | 不能静默取bind pose或丢掉组件后报完整成功 |
| EXPORT-BINARY | 7400 Binary头/GlobalSettings/Documents/Definitions/Objects/Connections/结束/footer | 01C writer探针实施中；完整05未完成、未验证 | 32位offset溢出提前拒绝；独立Blender与Unity原生消费者 |
| EXPORT-GEOMETRY | 三范围、层级/名字、实例/三角/normal/UV0/UV1/材质槽及相对贴图 | 完整05未实施、未验证 | 世界关键点/单位/法线/镜像/槽位/移出源路径验证，纯灯光不成功 |
| EXPORT-LOSS | 顶点色/切线能映射则写出，未实现或PBR不可靠映射逐项报告 | 完整05未实施、未验证 | 不静默丢失属性；不得用警告绕过必需基础几何/依赖 |
| EXPORT-TXN | 完整产物集暂存、检查hash、原子发布、提交点/取消/失败恢复 | 完整05未实施、未验证 | FBX/纹理/报告任一失败保留旧成功集合，导出不清工程dirty |

该表不列具体fixture路径，因为00C/01C在独立目录建立并记录真实hash；交接时将能力ID关联到实际fixture索引，不能将空路径或设计用例计作已执行。原创AffineProbeFixture仅验证IR与几何，不是格式二进制正样本，不替代独立来源FBX/GLB/OBJ。

未实现的承诺目标必须保留为未完成。拒绝非法输入可以得到该拒绝用例Passed；正常目标特征被拒绝则目标仍未完成，不能借安全拒绝关闭03。改变三格式/FBX输出等明确范围须由用户决定；数值预算/工程参数调整由协调者记录来源、影响与重验范围。
