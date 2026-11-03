# VRC-00C 原创夹具与依据

日期：2026-11-03。状态：通过（设计与输入交付）；没有提前关闭03的完整格式矩阵。

进入条件：00B接口和格式目标明确。使用独立Blender5.2.2 LTS后台进程与原创Python几何，不操作用户live Blender或源ShoppingDash资产。

交付包括：

- `Tools/VRC/Fixtures/index.json`：五个FBX正样本、六个负例、原始Blender工程/原创纹理、生成器与独立expected，逐文件hash。
- `Tools/VRC/Fixtures/BuildingBlocks/index.json`：窗洞墙、L形凹面、共享源mesh与不同实例材质、中文/空格贴图、FBX/GLB/OBJ/MTL，供后续生产导入验证。
- [验证矩阵](../contracts/validation-matrix.md)：25子批次与21用例族，已区分初始目标、首批子集与后续完整门槛。
- `Tools/VRC/References/{README.md,index.json}`：12份实际获取的公开资料；包含此前未取得的OBJ B1附录、MTL章节、glTF官方发布版HTML/PDF与当前源、FBX编码/变换/token依据。5次失败请求单列，未冒充参考正文。

29/29夹具摘要只读复核一致，详见`References/fixture-audit.json`。独立预期来自作者端世界点、每面拓扑/材质、每角UV/法线；不是自写reader/writer互读的结果。非零pivot样本有独立expected；另一个GeometricRotation+非均匀比例差异保留输入与失败证据，交03核对完整矩阵。

01已实际使用其中五个FBX输入和15个guards，在真实Player及双消费者取得证据；该结论见01日志。OBJ/GLB尚未实现，构件样本不是正式共享墙/门洞编辑语义或可分发库。外部参考归档位于Assets外，不参与运行程序集或Player打包；本地档案、构建和原始运行证据不自动纳入用户签入清单。

本批没有引入第三方生产库、修改旧工程或SCM提交/签入。下一门槛为02程序构件编辑与03生产解析；后续对夹具改动必须生成新索引与运行标识，保留旧失败证据。
