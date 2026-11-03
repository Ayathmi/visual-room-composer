# VRC-00C 验证矩阵与原创夹具设计

日期：2026-11-03。对应[分批部署指引](../visual-room-composer分批部署指引.md)第5、13—14节与[实施规划](../visual-room-composer实施规划.md)第6—12节。本文覆盖25个子批次和21个用例族。00A—01C已按实际设计/运行门槛关闭；当前结果见批次总览与01日志，未把旧ShoppingDash数量当新工具结果。用例族的完整终验范围尚未关闭，初始NotRun不表示01子集从未运行。

## 1. 统一结果与证据合同

- Passed：在记录的代码/包/后端/候选/fixture摘要上，期望集合非空且所有必需结果已结束并满足合同；预期拒绝必须同时证明无副作用。
- Failed：行为或资源/文件结果偏离合同；保留第一次失败与后续修复证据。
- NotRun：尚无本项所需实际执行证据。尚未执行的目标初始为此状态。
- Blocked/Skipped：明确原因；必需项仍阻止所属门槛。NotApplicable仅可引用正式范围合同，不能将尚未实现能力标不适用。
- 静态审计、纯数据/EditMode、PlayMode、真实Player、人工键鼠/IME、Blender与Unity原生导入分别留证。异步job、0/0、Total=0、退出码0或文件存在均不是完成证明。

每次运行记录caseId、batchId、inputRevision/hash、engine/package/backend、machine、fixtureId/hash、开始/结束、实际命令/选项、结果、误差/计数、诊断和证据路径。运行目录为`Validation/<子批次>/<唯一运行标识>/`；只创建实际证据，不用空XML或空截图占位。候选验收额外绑定exe/候选manifest摘要。恢复动作、外部修改及重试使用新runId，失败证据不覆盖。

## 2. 25个子批次门槛索引

00A—00C按设计交付门槛通过，01A—01C按05版真实Player及双消费者证据通过。其余仍NotRun；具体版本、失败与限制见批次总览和01日志。

| 子批次 | 上游依赖 | 验证责任 / 门槛 | 必需证据 | 结果 |
| --- | --- | --- | --- | --- |
| VRC-00A | 两份规划/授权边界 | 源目标基线、逐文件决定、最小闭包、来源/禁止依赖 | 当前源hash、主控现场baseline、提取表 | Passed |
| VRC-00B | 00A | 完整DTO/IR、接口、错误、版本/资源所有权、独立历史/导出范围合同 | 合同检查和消费者反馈；不能靠空类证明 | Passed |
| VRC-00C | 00B；来源设计可并行 | 独立正负夹具、预期数值、来源/生成器/hash、验证矩阵 | 原创输入+独立oracle；格式依据归档 | Passed |
| VRC-01A | 00A—00C | ENV/LIFE：PanelRenderer、文件框、真实Windows后端与正常退出 | 编译、Development Player日志/结果、必要输入观察 | Passed |
| VRC-01B | 01A；纯数据可提前 | XFORM：完整仿射/单位/镜像/剪切、共享变换/租约 | 独立数值预期、纯数据和Player世界几何 | Passed |
| VRC-01C | 01B、独立fixture | FBX/XFORM/INTEROP：7400/7500 ASCII/Binary最小读取与7400输出 | Player输出、Blender与Unity原生消费者两份报告 | Passed |
| VRC-02A | 01全部通过、合同同步 | HISTORY/EDIT：原子命令、独立时间线、组关系 | 规则测试、保存点/预算/取消结果 | NotRun |
| VRC-02B | 02A | EDIT/GEOM/XFORM：笔刷/选区/墙/附件/增量视口 | EditMode/PlayMode、Player操作/取消 | NotRun |
| VRC-02C | 02A；完整门槛使用02B样间 | STORE/LIB/HISTORY：真实库事务、保存/恢复/固定版本 | 隔离故障注入、Player退出重开与hash | NotRun |
| VRC-03A | 01全部通过 | INPUT/LIFE/XFORM：有界管线、resolver、代次与资源 | 负例和取消副作用、CPU/Unity资源计数 | NotRun |
| VRC-03B | 03A | OBJ/INPUT/LIFE：全部已承诺OBJ/MTL子集 | Reader与Player材质/纹理、负例取消 | NotRun |
| VRC-03C | 03A | GLB/INPUT/LIFE：全部已承诺GLB子集 | Reader与Player材质/透明/双面、扩展拒绝 | NotRun |
| VRC-03D | 03A、01C | FBX/XFORM/INPUT/LIFE：完整目标版本/变换/层映射 | 独立来源Reader/Player矩阵，未实现目标不关批 | NotRun |
| VRC-03E | 03B/03C/03D、02C | 三格式真实入库、重开/复制/链接、取消迟到与引用保留 | 实际Player生产服务；模拟提交不满足 | NotRun |
| VRC-04A | 02全部通过 | UI-LAYOUT：新主题、小大窗口、DPI/文字缩放、中英 | 运行截图与人工检查、Player字体资源 | NotRun |
| VRC-04B | 04A、02B/02C | UI-INPUT/HISTORY/STORE：活动撤销、焦点/IME/模态/失焦 | 自动事件和真实键鼠/IME分离证据 | NotRun |
| VRC-04C | 04B、03E、05C | 真实UI导入→搭建→存盘重启→三范围导出 | 可见控件生产接线、错误修复、导出不清dirty | NotRun |
| VRC-05A | 02、03E、01C | EXPORT-SCOPE/XFORM：固定快照/范围/祖先/附件/原点 | 精确placement集合、世界点、快照资源lease | NotRun |
| VRC-05B | 05A | EXPORT-DATA：Binary7400完整结构、材质/贴图及损失报告 | Player产物结构/offset/引用检查 | NotRun |
| VRC-05C | 05B | EXPORT-TXN/INTEROP：原子发布、取消、双消费者 | 独立目录产物，两消费者报告，旧成功集合hash | NotRun |
| VRC-06A | 02、03E、05C | MIGRATE/GEOM/STORE：源schema/固定引用/去游戏化 | 转换前后源hash、ID/丢弃项映射、Player | NotRun |
| VRC-06B | 06A、04C | 原创样间/来源表，双房间/共享墙/附件/三格式输入 | 用户流程、可搬迁工程/库、权限/署名记录 | NotRun |
| VRC-07A | 00—06全部所属子批通过 | 联合全部族+PERF，1000/5000实例与100轮生命周期 | 完整XML、真实Development Player、机器/资源原始数据 | NotRun |
| VRC-07B | 07A | RELEASE：非Development Windows候选与依赖审计 | 构建日志、candidate manifest/hash、后端/包/许可 | NotRun |
| VRC-07C | 07B当前候选 | 全部必需终验，三格式→存盘重启→FBX→两消费者 | 该候选UI/Player/人工/互操作/代表性能证据 | NotRun |

04C依赖05C，05不依赖完整04C；03E依赖真实02C入库。源/Unity/公共接口/最终构建由协调者串行持有，纯Reader分支在冻结合同后可并行。所有25行都是子批次，不把25当测试数量。

## 3. 21个用例族与稳定子ID

下列子ID表示必须展开的可追踪场景；一个子ID可有多个参数化输入。未生成夹具与未运行测试分别记录，不能以拒绝负例成功冒充对应格式正常导入成功。

| 用例族 / 子ID | 要证明的输入与观察结果 | 独立预期 / fixture | 首次 → 最终责任 | 证据层 | 结果 |
| --- | --- | --- | --- | --- | --- |
| ENV | ENV-01 PanelRenderer/字体/中英；02文件框打开/保存/取消/中文空格长路径/owner；03后端Task+JSON+压缩；04正常退出 | 目标7000.0.0a12真实Player；不能用EditorUtility | 01A → 07C | 编译+Player+人工 | NotRun |
| XFORM | XFORM-01单位/轴；02 meshTransform+pivot+GroundPivotOffset；03层级非均匀缩放/剪切；04负行列式/法线/切线；05奇异矩阵拒绝 | FBX-PROBE-BLENDER-7400独立作者世界点；其他手算矩阵金样 | 01B/01C → 03D/05C/07C | 独立数值+Player+消费者 | NotRun |
| EDIT | EDIT-01笔刷插值去重/一笔一次；02墙边吸附；03矩形完整实例；04多选四转/中心不漂移；05共享墙/宿主附件；06取消不提交 | 程序构件及ROOM-SHARED；精确ID、占地/前后状态 | 02B → 04C/07C | EditMode+PlayMode+Player | NotRun |
| HISTORY | HISTORY-01失败全回滚/前态冲突；02工程库文本撤销分离；03预算/分支/无变化；04跳转取消/迟到；05保存点；06显式库升级撤销 | 独立命令前后状态/字节；旧SddHistoryTests仅参考 | 02A → 04B/07C | 规则+真实交互 | NotRun |
| GEOM | GEOM-01五种floor mask；02凹面/带洞墙；03墙角/T/十字；04贴地/悬挂/附件支撑；05语义草稿可保存而结构错拒绝 | WALL-OPENING、CONCAVE、ROOM-SHARED；面积/射线/边界预期 | 02B → 06B/07C | 几何数据+视口 | NotRun |
| STORE | STORE-01同目录原子保存/前态hash；02外部改写/锁/磁盘/权限；03当前上一代恢复副本；04正常退出重开/新用户目录 | 隔离复制目录、原成功文件hash与dirty/savepoint | 02C → 07C | 故障注入+Player | NotRun |
| LIB | LIB-01 ID/revision/hash不可变/异hash冲突；02复制/链接/依赖闭包；03重定位；04多仓库移除登记；05显式升级撤销 | 固定库manifest、旧新版本hash、原文件不变 | 02C → 03E/04C | 数据/文件+Player | NotRun |
| INPUT | INPUT-01伪后缀/截断/越界；02checked溢出/压缩炸弹；03路径根/符号链接/UNC/URL；04缺依赖/替换；05总预算/深度/像素 | 由正例复制派生负例，记录变异偏移/预期错误阶段及无副作用 | 03A → 03B—03E | 有界读取+隔离副作用 | NotRun |
| LIFE | LIFE-01逐阶段取消/软超时；02切文档迟到；03重复缓存/并发读取；04父实例提前释放；05历史/导出持有lease；06重复释放/退出 | 任务终态唯一、可测取消延迟、对象/文件锁/稳态内存计数 | 01A → 03E/07A/07C | PlayMode+Player+资源 | NotRun |
| OBJ | OBJ-01四种face形式/负索引；02独立UV/法线/平滑组；03凹面；04多MTL/map参数；05中英空格路径/单位上轴；06错误面拒绝 | FORMAT-OBJ，独立手写语法+几何/UV预期；不能生产writer自证 | 03B → 07C | Reader+Player+FBX互操作 | NotRun |
| GLB | GLB-01 chunk/对齐/offset/stride；02 normalized/sparse；03索引/无索引/strip/fan；04 UV1/颜色/PBR通道/透明双面；05嵌入外部图；06 required扩展 | FORMAT-GLB，独立生成器/作者场景；每项明确是否支持或拒绝 | 03C → 07C | Reader+Player+FBX互操作 | NotRun |
| FBX | FBX-01 7400/7500 ASCII/Binary；02压缩/未压缩；03Objects/Connections环/坏引用；04 layer/direct/index/别名；05模板/旋转序/pivot/geometric/InheritType；06材质纹理 | 独立Blender正例+独立语法生成器，各能力有样例；普通TRS不替代继承 | 01C → 03D/07C | Reader+Player+独立来源 | NotRun |
| UI-LAYOUT | UI-LAYOUT-01 1280x720/1080p/4K；02 DPI100/150/200%；03文字200%/折叠滚动；04中英/主题对比/单主操作 | 实际运行截图/关键控件可达；Apple网页按已确认桌面适配 | 04A → 07C | 动态截图+人工 | NotRun |
| UI-INPUT | UI-INPUT-01中文IME/文本撤销；02Tab/焦点；03模态/原生文件框；04失焦/切房间取消；05从UI开始拖拽不穿透；06快捷键冲突 | 真实键鼠/IME与注入事件分别取证；V/B/W/R/F与Ctrl+D实际一致 | 04B → 07C | 自动事件+人工 | NotRun |
| EXPORT-SCOPE | EXPORT-SCOPE-01全工程/当前房间/选择；02组递归/必要父级；03共享墙去重/附件闭包；04隐藏与参与导出分离；05工程原点；06导出中编辑固定快照 | ROOM-SHARED精确placement集合/预期世界点；快照同时锁资源版本 | 05A → 07C | 集合/坐标+Player | NotRun |
| EXPORT-DATA | EXPORT-DATA-01 header/offset/footer/ID引用；02层级实例/网格槽位；03UV0/UV1/法线；04相对纹理/重名；05颜色切线/PBR损失报告；06纯灯光不伪成功 | ModelScene快照与独立消费者结构；不能以自写reader往返为唯一oracle | 05B → 07C | 文件结构+Player+材质人工 | NotRun |
| EXPORT-TXN | EXPORT-TXN-01写FBX/纹理/报告/发布阶段故障；02取消提交点；03hash冲突/权限/锁/磁盘/跨卷；04旧产物和dirty不变 | 隔离目录前后hash；成功集合完整，失败无成功标记，已提交取消终态一致 | 05C → 07C | 故障注入+文件摘要 | NotRun |
| INTEROP | INTEROP-01迁出源/缓存目录；02 Blender读输出；03 Unity原生读输出；04三输入和三输出范围高风险组合 | 两个独立消费者版本/选项/结果；三角数/槽位/关键点/法线/UV及贴图 | 01C → 05C/07C | 两消费者+人工材质 | NotRun |
| MIGRATE | MIGRATE-01 format/schema识别真实/旧扩展名；02固定Profile/依赖缺失；03Shelf/SearchContainer静态化；04移除游戏字段；05源hash与ID映射 | 自有旧schema样例先验，再显式转换；不回写/自动升级任何旧修订 | 06A → 06B/07C | 转换对照+Player | NotRun |
| PERF | PERF-01 1000/5000实例共享/非共享；02帧P95/P99/点击延迟/主线程停顿；03 100轮导入预览释放；04开关文档/撤销/升级/取消 | 记录机器/三角纹理数量、CPU/GPU/托管原生估算与对象计数；退出诊断单列 | 07A → 07C | 实际Player/候选原始数据 | NotRun |
| RELEASE | RELEASE-01非Development/backend/PlayerLog；02无禁止程序集/SDK/源路径依赖；03shader字体/裁剪；04候选hash和许可证据；05正常退出/隔离用户目录 | candidate manifest、源码/资源输入摘要、实际候选闭环 | 07B → 07C | 构建+成品审计+候选运行 | NotRun |

数值起始门槛采用主规划提案：米制小型金样关键点误差≤1e-4m，法线≤0.1°；大模型同时记录相对误差；材质不做跨渲染器逐像素相等断言。1080p交互目标60FPS、P95≤16.67ms/P99≤33.3ms、点击反馈≤50ms；阈值仍须绑定机器/数据规模。调整先更新正式合同，不能测后改低门槛再宣称原目标通过。

## 4. 原创来源与金样审核

### 4.1 当前可检查的独立来源

`Tools/VRC/Fixtures/create_blender_fixture.py`以显式顶点/面和数学变换创建原创几何，以4x4数值像素生成棋盘纹理；本次全文检查生成器及expected.json，未发现导入商业模型/纹理或调用产品FBX writer。两对象各有4个三角面、两个材质槽，合计8个三角面；一个对象有负比例，父层级有23度旋转及非均匀比例，面角UV独立。世界点由Blender作者场景矩阵计算并存于expected，坐标明确为Blender原生右手Z-up米制。

当前[fixture索引](D:/DevOpus/visual-room-composer/Tools/VRC/Fixtures/index.json)已列四个基础版本ID：`FBX-PROBE-BLENDER-7400`、`FBX-PROBE-BLENDER-7400-ASCII`、`FBX-PROBE-BLENDER-7500`、`FBX-PROBE-BLENDER-7500-ASCII`。后三者是同一原创作者场景的独立测试树编码变体，不能算四个不同几何来源。索引另已登记`FBX-PROBE-PIVOT-7400`及6个FBX负例；本表仍不据生成状态推断产品运行通过。这证明输入来源和预期构造可审查，不证明产品Reader、坐标规范化、Player、writer或互操作通过。Blender生成器是测试工具，不进入产品运行依赖。具体样例与最终fixture ID/hash以该真实索引为准；本文件不修改其生成器或索引。

| 当前原始文件（目标工程相对路径） | 字节 | SHA256 | 用途 |
| --- | ---: | --- | --- |
| [Tools/VRC/Fixtures/create_blender_fixture.py](<D:/DevOpus/visual-room-composer/Tools/VRC/Fixtures/create_blender_fixture.py>) | 4396 | `a4d840643e23c33ec1279a132fb4247d83db16c27b7aba2f5a46ca111c351ddc` | 独立来源；01探针已使用并通过，完整格式终验未关闭 |
| [Tools/VRC/Fixtures/Generated/blender-original.blend](<D:/DevOpus/visual-room-composer/Tools/VRC/Fixtures/Generated/blender-original.blend>) | 98014 | `a65813d9d4a754c4692a02ad14d6d8bf67b56b015868fbdfb4dcb5c84cfdadaf` | 独立来源；01探针已使用并通过，完整格式终验未关闭 |
| [Tools/VRC/Fixtures/Generated/blender-original-7400.fbx](<D:/DevOpus/visual-room-composer/Tools/VRC/Fixtures/Generated/blender-original-7400.fbx>) | 24524 | `a2f2b84515d655631565538e71dff33b9c13b022b6d4830f5c2b8bfc5a6efd8f` | 独立来源；01探针已使用并通过，完整格式终验未关闭 |
| [Tools/VRC/Fixtures/Generated/original-checker.png](<D:/DevOpus/visual-room-composer/Tools/VRC/Fixtures/Generated/original-checker.png>) | 262 | `1360bfe5e0dd444a870c286005b4a0e98cd759ae9cf64191224c8656d1649e88` | 独立来源；01探针已使用并通过，完整格式终验未关闭 |
| [Tools/VRC/Fixtures/Generated/blender-original.expected.json](<D:/DevOpus/visual-room-composer/Tools/VRC/Fixtures/Generated/blender-original.expected.json>) | 10068 | `1944df984220e816ee28f9ca806ae85bd7bf4edc3d80504a22250bb51f8c3f6c` | 独立来源；01探针已使用并通过，完整格式终验未关闭 |

来源设计审核结论：可作为01最小复杂静态变换探针的独立输入；当前这一个四面体场景不能替代带洞墙、中文路径、全部层映射、所有InheritType/旋转序、完整OBJ/GLB矩阵或三范围导出验收。对象/材质/顶点拆分数量按各格式允许的属性拆分比较，不要求各消费者都保留相同顶点编号。

### 4.2 必须补齐的独立样例族

| 设计ID | 原创构造 / 独立预期 | 目标覆盖 | 设计落实状态 / 运行结果 |
| --- | --- | --- | --- |
| SCALE-AXIS | 手工非对称米制标尺，三个轴不同长度、偏离原点；留原始顶点和世界坐标 | XFORM单位/轴/原点 | 待生成；NotRun |
| WALL-OPENING | 4m宽、3m高、0.25m厚墙，中央1m宽2m高洞口，以分离左右墙柱和过梁建网格；洞口中心射线不命中、柱体射线命中，预期几何显式存表 | GEOM门洞/法线/导出真实开口 | 窗洞基础输入已生成；本行门洞目标仍待补；NotRun |
| CONCAVE | XZ平面L形轮廓(0,0),(2,0),(2,1),(1,1),(1,2),(0,2)，简单凹六边形；面积3m²、4三角形、不得填入缺口 | OBJ/FBX三角化、GEOM | 独立Blender基础输入与面积预期已生成；NotRun |
| FORMAT-OBJ | 独立手写v/vt/vn/f文件，同位置不同UV/法线角、正负索引、多MTL/两材质，依赖在中文空格目录 | OBJ全部目标分组，路径resolver | 基础OBJ/MTL及中文贴图已生成；索引/平滑等变体待补；NotRun |
| FORMAT-GLB | 独立于产品writer的字节生成器或作者场景导出；逐例offset/stride/sparse/normalized、strip/fan、PBR通道/UV1等，预期字节与几何单列 | GLB全部目标分组 | 基础GLB来源已生成；accessor/拓扑等变体待补；NotRun |
| FBX-VERSIONS | 独立语法生成器生成7400/7500 ASCII/Binary及压缩数组；另由Blender等独立来源覆盖复杂变换；实际IDs由fixture索引登记 | FBX编码/层/连接/继承 | 五个版本/编码/pivot正例在01已通过；其他层/继承变体待补，完整族NotRun |
| ROOM-SHARED | 两个显式房间、一条共享墙、一门洞/附件、一悬挂Prop、一个嵌套组、隐藏参与导出对象、排除对象；预写三范围ID集合/必要父节点 | EDIT/GEOM/EXPORT-SCOPE | 仅共享几何来源已生成；房间/组/共享墙/附件语义待补；NotRun |
| LEGACY-OWNED | 用原创构件创建旧SDD schema最小节点/工程/Profile，固定hash；包含要剔除的库存/锚点及明确映射 | MIGRATE | 待生成；NotRun |
| INPUT-MUTATIONS | 正例复制后只修改已知偏移/字段，截断、溢出、深度/元素预算、坏连接、依赖逃逸/别名冲突；变异清单不针对用户源文件 | INPUT/LIFE/格式负例 | 6个FBX负例及15个guards已在01运行；其他格式/预算组合待补，完整族NotRun |

当前新增基础来源见[BuildingBlocks/index.json](D:/DevOpus/visual-room-composer/Tools/VRC/Fixtures/BuildingBlocks/index.json)。其WALL-OPENING是4×3×0.2米墙及窗洞（x=-0.6..0.8、源高度0.7..2.2米），不同于上表仍需补齐的落地门洞目标；ROOM-SHARED只证明作者场景共享网格和镜像实例反转材质槽，不包含RoomDocument的双房间归属/共享墙/附件和导出范围语义。命名相同不等于覆盖相同验收。

每个fixture索引项必须有：ID、来源/作者或生成器、源/生成器hash、生成命令和工具版本、产物hash、单位/轴/材料/UV/关键点独立预期、适用/不适用能力、预期拒绝阶段/错误码和允许副作用。hash必须读取真实文件后填写；未生成的设计不预填假hash。

00C所需格式依据已归档至`Tools/VRC/References/`，00B接口/错误码与真实fixture索引已核对；29/29夹具摘要一致。该设计交付由实际文件和审核记录关闭，不将其当成03完整矩阵运行通过。

## 5. 独立消费者与故障运行设计

Blender和Unity Editor原生FBX路径都要记录可执行文件/Editor版本、导入选项、轴/单位转换、输入产物摘要、解析对象/材质/三角计数、关键点/法线/UV误差和缺失纹理。一次成功由一份消费者报告证明，不能用一个工具的输出代替另一个。自写reader回读仅附加内部一致性证据。

复制输出至独立目录后断开源/缓存依赖再读；只能操作本次拥有的测试目录，不改源ShoppingDash资产、不锁用户工作文件、不真实耗尽用户磁盘。磁盘满/权限/提交失败优先使用可控故障注入，真实文件框/路径/取消再在隔离目录验证；预期错误应报告阶段/文件/偏移或节点/修复建议。

每个取消用例核对唯一终态、任务是否真正停止、迟到结果不会写新文档、候选没有入库、主/后台资源与文件锁释放、旧产物hash不变；发布提交后按已提交终态报告。预算用例以有限小预算触发，不依靠巨型真实文件造成无界分配。

当前00A—01C已关闭，01探针已实际编译并完成05版Player39/39、guards15/15及迁出后的两消费者各5/5。02—07与21族的完整产品终验仍未关闭；本轮没有运行Unity EditMode/PlayMode套件、正式人工IME/视觉或性能/耐久测试。实际证据见批次总览与01日志。
