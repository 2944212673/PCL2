﻿Public Module ModDownload

#Region "DlClient* | Minecraft 客户端"

    ''' <summary>
    ''' 返回某 Minecraft 版本对应的原版主 Jar 文件的下载信息，要求对应依赖版本已存在。
    ''' 失败则抛出异常，不需要下载则返回 Nothing。
    ''' </summary>
    Public Function DlClientJarGet(Version As McVersion, ReturnNothingOnFileUseable As Boolean) As NetFile
        '获取底层继承版本
        Do While Not String.IsNullOrEmpty(Version.InheritVersion)
            Version = New McVersion(Version.InheritVersion)
        Loop
        '检查 Json 是否标准
        If Version.JsonObject("downloads") Is Nothing OrElse Version.JsonObject("downloads")("client") Is Nothing OrElse Version.JsonObject("downloads")("client")("url") Is Nothing Then
            Throw New Exception("底层版本 " & Version.Name & " 中无 jar 文件下载信息")
        End If
        '检查文件
        Dim Checker As New FileChecker(MinSize:=1024, ActualSize:=If(Version.JsonObject("downloads")("client")("size"), -1), Hash:=Version.JsonObject("downloads")("client")("sha1"))
        If ReturnNothingOnFileUseable Then
            '是否跳过
            Dim IsSetupSkip As Boolean = ShouldIgnoreFileCheck(Version)
            If IsSetupSkip AndAlso File.Exists(Version.Path & Version.Name & ".jar") Then Return Nothing '跳过校验
            If Checker.Check(Version.Path & Version.Name & ".jar") Is Nothing Then Return Nothing '通过校验
        End If
        '返回下载信息
        Dim JarUrl As String = Version.JsonObject("downloads")("client")("url")
        Return New NetFile(DlSourceLauncherOrMetaGet(JarUrl), Version.Path & Version.Name & ".jar", Checker)
    End Function

    ''' <summary>
    ''' 返回某 Minecraft 版本对应的原版主 AssetIndex 文件的下载信息，要求对应依赖版本已存在。
    ''' 若未找到，则会返回 Legacy 资源文件或 Nothing。
    ''' </summary>
    Public Function DlClientAssetIndexGet(Version As McVersion) As NetFile
        '获取底层继承版本
        Do While Not String.IsNullOrEmpty(Version.InheritVersion)
            Version = New McVersion(Version.InheritVersion)
        Loop
        '获取信息
        Dim IndexInfo = McAssetsGetIndex(Version, True, True)
        Dim IndexAddress As String = PathMcFolder & "assets\indexes\" & IndexInfo("id").ToString & ".json"
        Log("[Download] 版本 " & Version.Name & " 对应的资源文件索引为 " & IndexInfo("id").ToString)
        Dim IndexUrl As String = If(IndexInfo("url"), "")
        If IndexUrl = "" Then
            Return Nothing
        Else
            Return New NetFile(DlSourceLauncherOrMetaGet(IndexUrl), IndexAddress, New FileChecker(CanUseExistsFile:=False, IsJson:=True))
        End If
    End Function

    ''' <summary>
    ''' 构造补全某 Minecraft 版本的所有文件的加载器列表。失败会抛出异常。
    ''' </summary>
    Public Function DlClientFix(Version As McVersion, CheckAssetsHash As Boolean, AssetsIndexBehaviour As AssetsIndexExistsBehaviour, SkipAssetsDownloadWhileSetupRequired As Boolean) As List(Of LoaderBase)
        Dim Loaders As New List(Of LoaderBase)

#Region "下载支持库文件"
        Dim LoadersLib As New List(Of LoaderBase) From {
                New LoaderTask(Of String, List(Of NetFile))("分析缺失支持库文件", Sub(Task As LoaderTask(Of String, List(Of NetFile))) Task.Output = McLibFix(Version)) With {.ProgressWeight = 1},
                New LoaderDownload("下载支持库文件", New List(Of NetFile)) With {.ProgressWeight = 15}
            }
        '构造加载器
        Loaders.Add(New LoaderCombo(Of String)("下载支持库文件（主加载器）", LoadersLib) With {.Block = False, .Show = False, .ProgressWeight = 16})
#End Region

#Region "下载资源文件"
        Dim IsSetupSkip As Boolean = ShouldIgnoreFileCheck(Version)
        If IsSetupSkip Then Log("[Download] 已跳过 Assets 下载")
        If (Not SkipAssetsDownloadWhileSetupRequired) OrElse Not IsSetupSkip Then
            Dim LoadersAssets As New List(Of LoaderBase)
            '获取资源文件索引地址
            LoadersAssets.Add(New LoaderTask(Of String, List(Of NetFile))("分析资源文件索引地址",
                                                                Sub(Task As LoaderTask(Of String, List(Of NetFile)))
                                                                    Try
                                                                        Dim IndexFile = DlClientAssetIndexGet(Version)
                                                                        Dim IndexFileInfo As New FileInfo(IndexFile.LocalPath)
                                                                        If AssetsIndexBehaviour <> AssetsIndexExistsBehaviour.AlwaysDownload AndAlso IndexFile.Check.Check(IndexFile.LocalPath) Is Nothing Then
                                                                            Task.Output = New List(Of NetFile)
                                                                        Else
                                                                            Task.Output = New List(Of NetFile) From {IndexFile}
                                                                        End If
                                                                    Catch ex As Exception
                                                                        Throw New Exception("分析资源文件索引地址失败", ex)
                                                                    End Try
                                                                End Sub) With {.ProgressWeight = 0.5, .Show = False})
            '下载资源文件索引
            LoadersAssets.Add(New LoaderDownload("下载资源文件索引", New List(Of NetFile)) With {.ProgressWeight = 2})
            '要求独立更新索引
            If AssetsIndexBehaviour = AssetsIndexExistsBehaviour.DownloadInBackground Then
                Dim LoadersAssetsUpdate As New List(Of LoaderBase)
                Dim TempAddress As String = Nothing
                Dim RealAddress As String = Nothing
                LoadersAssetsUpdate.Add(New LoaderTask(Of String, List(Of NetFile))("后台分析资源文件索引地址", Sub(Task As LoaderTask(Of String, List(Of NetFile)))
                                                                                                        Dim BackAssetsFile As NetFile = DlClientAssetIndexGet(Version)
                                                                                                        RealAddress = BackAssetsFile.LocalPath
                                                                                                        TempAddress = PathTemp & "Cache\" & BackAssetsFile.LocalName
                                                                                                        BackAssetsFile.LocalPath = TempAddress
                                                                                                        Task.Output = New List(Of NetFile) From {BackAssetsFile}
                                                                                                    End Sub))
                LoadersAssetsUpdate.Add(New LoaderDownload("后台下载资源文件索引", New List(Of NetFile)))
                LoadersAssetsUpdate.Add(New LoaderTask(Of List(Of NetFile), String)("后台复制资源文件索引", Sub(Task As LoaderTask(Of List(Of NetFile), String))
                                                                                                      CopyFile(TempAddress, RealAddress)
                                                                                                      McLaunchLog("后台更新资源文件索引成功：" & TempAddress)
                                                                                                  End Sub))
                Dim Updater As New LoaderCombo(Of String)("后台更新资源文件索引", LoadersAssetsUpdate)
                Log("[Download] 开始后台更新资源文件索引")
                Updater.Start()
            End If
            '获取资源文件地址
            LoadersAssets.Add(New LoaderTask(Of String, List(Of NetFile))("分析缺失资源文件",
                                                                Sub(Task As LoaderTask(Of String, List(Of NetFile)))
                                                                    Task.Output = McAssetsFixList(McAssetsGetIndexName(Version), CheckAssetsHash, Task)
                                                                End Sub) With {.ProgressWeight = 3})
            '下载资源文件
            LoadersAssets.Add(New LoaderDownload("下载资源文件", New List(Of NetFile)) With {.ProgressWeight = 25})
            '构造加载器
            Loaders.Add(New LoaderCombo(Of String)("下载资源文件（主加载器）", LoadersAssets) With {.Block = False, .Show = False, .ProgressWeight = 30.5})
        End If
#End Region

        Return Loaders
    End Function
    Public Enum AssetsIndexExistsBehaviour
        ''' <summary>
        ''' 如果文件存在，则不进行下载。
        ''' </summary>
        DontDownload
        ''' <summary>
        ''' 如果文件存在，则启动新的下载加载器进行独立的更新。
        ''' </summary>
        DownloadInBackground
        ''' <summary>
        ''' 如果文件存在，也同样进行下载。
        ''' </summary>
        AlwaysDownload
    End Enum

#End Region

#Region "DlClientList | Minecraft 客户端 版本列表"

    '主加载器
    Public Structure DlClientListResult
        ''' <summary>
        ''' 数据来源名称，如“Mojang”，“BMCLAPI”。
        ''' </summary>
        Public SourceName As String
        ''' <summary>
        ''' 是否为官方的实时数据。
        ''' </summary>
        Public IsOfficial As Boolean
        ''' <summary>
        ''' 获取到的 Json 数据。
        ''' </summary>
        Public Value As JObject
        '''' <summary>
        '''' 官方源的失败原因。若没有则为 Nothing。
        '''' </summary>
        'Public OfficialError As Exception
    End Structure
    ''' <summary>
    ''' Minecraft 客户端 版本列表，主加载器。
    ''' </summary>
    Public DlClientListLoader As New LoaderTask(Of Integer, DlClientListResult)("DlClientList Main", AddressOf DlClientListMain)
    Private Sub DlClientListMain(Loader As LoaderTask(Of Integer, DlClientListResult))
        Select Case Setup.Get("ToolDownloadVersion")
            Case 0
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)(DlClientListBmclapiLoader, 30),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)(DlClientListMojangLoader, 60)
                }, Loader.IsForceRestarting)
            Case 1
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)(DlClientListMojangLoader, 5),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)(DlClientListBmclapiLoader, 35)
                }, Loader.IsForceRestarting)
            Case Else
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)(DlClientListMojangLoader, 60),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlClientListResult), Integer)(DlClientListBmclapiLoader, 60)
                }, Loader.IsForceRestarting)
        End Select
    End Sub

    '各个下载源的分加载器
    ''' <summary>
    ''' Minecraft 客户端 版本列表，Mojang 官方源加载器。
    ''' </summary>
    Public DlClientListMojangLoader As New LoaderTask(Of Integer, DlClientListResult)("DlClientList Mojang", AddressOf DlClientListMojangMain)
    Private IsNewClientVersionHinted As Boolean = False
    Private Sub DlClientListMojangMain(Loader As LoaderTask(Of Integer, DlClientListResult))
        Dim Json As JObject = NetGetCodeByRequestRetry("https://launchermeta.mojang.com/mc/game/version_manifest.json", IsJson:=True)
        Try
            Dim Versions As JArray = Json("versions")
            If Versions.Count < 200 Then Throw New Exception("获取到的版本列表长度不足（" & Json.ToString & "）")
            '添加 PCL 特供项
            If File.Exists(PathTemp & "Cache\download.json") Then Versions.Merge(GetJson(ReadFile(PathTemp & "Cache\download.json")))
            '返回
            Loader.Output = New DlClientListResult With {.IsOfficial = True, .SourceName = "Mojang 官方源", .Value = Json}
            '解析更新提示（Release）
            Dim Version As String = Json("latest")("release")
            If Setup.Get("ToolUpdateRelease") AndAlso Not Setup.Get("ToolUpdateReleaseLast") = "" AndAlso Version IsNot Nothing AndAlso Not Setup.Get("ToolUpdateReleaseLast") = Version Then
                McDownloadClientUpdateHint(Version, Json)
                IsNewClientVersionHinted = True
            End If
            McVersionHighest = Version.Split(".")(1)
            Setup.Set("ToolUpdateReleaseLast", Version)
            '解析更新提示（Snapshot）
            Version = Json("latest")("snapshot")
            If Setup.Get("ToolUpdateSnapshot") AndAlso Not Setup.Get("ToolUpdateSnapshotLast") = "" AndAlso Version IsNot Nothing AndAlso Not Setup.Get("ToolUpdateSnapshotLast") = Version AndAlso Not IsNewClientVersionHinted Then
                McDownloadClientUpdateHint(Version, Json)
            End If
            Setup.Set("ToolUpdateSnapshotLast", If(Version, "Nothing"))
        Catch ex As Exception
            Throw New Exception("版本列表解析失败", ex)
        End Try
    End Sub
    ''' <summary>
    ''' Minecraft 客户端 版本列表，BMCLAPI 源加载器。
    ''' </summary>
    Public DlClientListBmclapiLoader As New LoaderTask(Of Integer, DlClientListResult)("DlClientList Bmclapi", AddressOf DlClientListBmclapiMain)
    Private Sub DlClientListBmclapiMain(Loader As LoaderTask(Of Integer, DlClientListResult))
        Dim Json As JObject = NetGetCodeByRequestRetry("https://bmclapi2.bangbang93.com/mc/game/version_manifest.json",
                                                       BackupUrl:="https://download.mcbbs.net/mc/game/version_manifest.json",
                                                       IsJson:=True)
        Try
            Dim Versions As JArray = Json("versions")
            If Versions.Count < 200 Then Throw New Exception("获取到的版本列表长度不足（" & Json.ToString & "）")
            '添加 PCL 特供项
            If File.Exists(PathTemp & "Cache\download.json") Then Versions.Merge(GetJson(ReadFile(PathTemp & "Cache\download.json")))
            '返回
            Loader.Output = New DlClientListResult With {.IsOfficial = False, .SourceName = "BMCLAPI", .Value = Json}
        Catch ex As Exception
            Throw New Exception("版本列表解析失败（" & Json.ToString & "）", ex)
        End Try
    End Sub

    ''' <summary>
    ''' 获取某个版本的 Json 下载地址，若失败则返回 Nothing。必须在工作线程执行。
    ''' </summary>
    Public Function DlClientListGet(Id As String)
        Try
            '确认 Minecraft 版本列表已完成获取
            Select Case DlClientListLoader.State
                Case LoadState.Loading
                    DlClientListLoader.WaitForExit()
                Case LoadState.Failed, LoadState.Aborted, LoadState.Waiting
                    DlClientListLoader.WaitForExit(IsForceRestart:=True)
            End Select
            '确认版本格式标准
            Id = Id.Replace("_", "-") '1.7.10_pre4 在版本列表中显示为 1.7.10-pre4
            If Id <> "1.0" AndAlso Id.EndsWith(".0") Then Id = Left(Id, Id.Length - 2) 'OptiFine 1.8 的下载会触发此问题，显示版本为 1.8.0
            '查找版本并开始
            For Each Version As JObject In DlClientListLoader.Output.Value("versions")
                If Version("id") = Id Then
                    Return Version("url").ToString
                End If
            Next
            Log("未发现版本 " & Id & " 的 json 下载地址，版本列表返回为：" & vbCrLf & DlClientListLoader.Output.Value.ToString, LogLevel.Debug)
            Return Nothing
        Catch ex As Exception
            Log(ex, "获取版本 " & Id & " 的 json 下载地址失败")
            Return Nothing
        End Try
    End Function

#End Region

#Region "DlOptiFineList | OptiFine 版本列表"

    Public Structure DlOptiFineListResult
        ''' <summary>
        ''' 数据来源名称，如“Official”，“BMCLAPI”。
        ''' </summary>
        Public SourceName As String
        ''' <summary>
        ''' 是否为官方的实时数据。
        ''' </summary>
        Public IsOfficial As Boolean
        ''' <summary>
        ''' 获取到的数据。
        ''' </summary>
        Public Value As List(Of DlOptiFineListEntry)
    End Structure
    Public Class DlOptiFineListEntry
        ''' <summary>
        ''' 显示名称，已去除 HD_U 字样，如“1.12.2 C8”。
        ''' </summary>
        Public NameDisplay As String
        ''' <summary>
        ''' 原始文件名称，如“preview_OptiFine_1.11_HD_U_E1_pre.jar”。
        ''' </summary>
        Public NameFile As String
        ''' <summary>
        ''' 对应的版本名称，如“1.13.2-OptiFine_HD_U_E6”。
        ''' </summary>
        Public NameVersion As String
        ''' <summary>
        ''' 是否为测试版。
        ''' </summary>
        Public IsPreview As Boolean
        ''' <summary>
        ''' 对应的 Minecraft 版本，如“1.12.2”。
        ''' </summary>
        Public Property Inherit As String
            Get
                Return _inherit
            End Get
            Set(value As String)
                If value.EndsWith(".0") Then value = Left(value, value.Length - 2)
                _inherit = value
            End Set
        End Property
        Private _inherit As String
        ''' <summary>
        ''' 发布时间，格式为“yyyy/mm/dd”。OptiFine 源无此数据。
        ''' </summary>
        Public ReleaseTime As String
        ''' <summary>
        ''' 需要的最低 Forge 版本。空字符串为无限制，Nothing 为不兼容，“28.1.56” 表示版本号，“1161” 表示版本号的最后一位。
        ''' </summary>
        Public RequiredForgeVersion As String
    End Class

    ''' <summary>
    ''' OptiFine 版本列表，主加载器。
    ''' </summary>
    Public DlOptiFineListLoader As New LoaderTask(Of Integer, DlOptiFineListResult)("DlOptiFineList Main", AddressOf DlOptiFineListMain)
    Private Sub DlOptiFineListMain(Loader As LoaderTask(Of Integer, DlOptiFineListResult))
        Select Case Setup.Get("ToolDownloadVersion")
            Case 0
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)(DlOptiFineListBmclapiLoader, 30),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)(DlOptiFineListOfficialLoader, 60)
                }, Loader.IsForceRestarting)
            Case 1
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)(DlOptiFineListOfficialLoader, 5),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)(DlOptiFineListBmclapiLoader, 35)
                }, Loader.IsForceRestarting)
            Case Else
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)(DlOptiFineListOfficialLoader, 60),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlOptiFineListResult), Integer)(DlOptiFineListBmclapiLoader, 60)
                }, Loader.IsForceRestarting)
        End Select
    End Sub

    ''' <summary>
    ''' OptiFine 版本列表，官方源。
    ''' </summary>
    Public DlOptiFineListOfficialLoader As New LoaderTask(Of Integer, DlOptiFineListResult)("DlOptiFineList Official", AddressOf DlOptiFineListOfficialMain)
    Private Sub DlOptiFineListOfficialMain(Loader As LoaderTask(Of Integer, DlOptiFineListResult))
        Dim Result As String = NetGetCodeByClient("https://optifine.net/downloads", Encoding.Default)
        If Result.Length < 200 Then Throw New Exception("获取到的版本列表长度不足（" & Result & "）")
        '获取所有版本信息
        Dim Forge As List(Of String) = RegexSearch(Result, "(?<=colForge'>)[^<]*")
        Dim ReleaseTime As List(Of String) = RegexSearch(Result, "(?<=colDate'>)[^<]+")
        Dim Name As List(Of String) = RegexSearch(Result, "(?<=OptiFine_)[0-9A-Za-z_.]+(?=.jar"")")
        If Not ReleaseTime.Count = Name.Count Then Throw New Exception("版本与发布时间数据无法对应")
        If Not Forge.Count = Name.Count Then Throw New Exception("版本与 Forge 兼容数据无法对应")
        If ReleaseTime.Count < 10 Then Throw New Exception("获取到的版本数量不足（" & Result & "）")
        '转化为列表输出
        Dim Versions As New List(Of DlOptiFineListEntry)
        For i = 0 To ReleaseTime.Count - 1
            Name(i) = Name(i).Replace("_", " ")
            Dim Entry As New DlOptiFineListEntry With {
                         .NameDisplay = Name(i).Replace("HD U ", "").Replace(".0 ", " "),
                         .ReleaseTime = Join({ReleaseTime(i).Split(".")(2), ReleaseTime(i).Split(".")(1), ReleaseTime(i).Split(".")(0)}, "/"),
                         .IsPreview = Name(i).ToLower.Contains("pre"),
                         .Inherit = Name(i).ToString.Split(" ")(0),
                         .NameFile = If(Name(i).ToLower.Contains("pre"), "preview_", "") & "OptiFine_" & Name(i).Replace(" ", "_") & ".jar",
                         .RequiredForgeVersion = Forge(i).Replace("Forge ", "").Replace("#", "")}
            If Entry.RequiredForgeVersion.Contains("N/A") Then Entry.RequiredForgeVersion = Nothing
            Entry.NameVersion = Entry.Inherit & "-OptiFine_" & Name(i).ToString.Replace(" ", "_").Replace(Entry.Inherit & "_", "")
            Versions.Add(Entry)
        Next
        Loader.Output = New DlOptiFineListResult With {.IsOfficial = True, .SourceName = "OptiFine 官方源", .Value = Versions}
    End Sub

    ''' <summary>
    ''' OptiFine 版本列表，BMCLAPI。
    ''' </summary>
    Public DlOptiFineListBmclapiLoader As New LoaderTask(Of Integer, DlOptiFineListResult)("DlOptiFineList Bmclapi", AddressOf DlOptiFineListBmclapiMain)
    Private Sub DlOptiFineListBmclapiMain(Loader As LoaderTask(Of Integer, DlOptiFineListResult))
        Dim Json As JArray = NetGetCodeByRequestRetry("https://bmclapi2.bangbang93.com/optifine/versionList",
                                                      BackupUrl:="https://download.mcbbs.net/optifine/versionList", IsJson:=True)
        Try
            Dim Versions As New List(Of DlOptiFineListEntry)
            For Each Token As JObject In Json
                Dim Entry As New DlOptiFineListEntry With {
                             .NameDisplay = (Token("mcversion").ToString & Token("type").ToString.Replace("HD_U", "").Replace("_", " ") & " " & Token("patch").ToString).Replace(".0 ", " "),
                             .ReleaseTime = "",
                             .IsPreview = Token("patch").ToString.ToLower.Contains("pre"),
                             .Inherit = Token("mcversion").ToString,
                             .NameFile = Token("filename").ToString,
                             .RequiredForgeVersion = If(Token("forge"), "").ToString.Replace("Forge ", "").Replace("#", "")
                         }
                If Entry.RequiredForgeVersion.Contains("N/A") Then Entry.RequiredForgeVersion = Nothing
                Entry.NameVersion = Entry.Inherit & "-OptiFine_" & (Token("type").ToString & " " & Token("patch").ToString).Replace(".0 ", " ").Replace(" ", "_").Replace(Entry.Inherit & "_", "")
                Versions.Add(Entry)
            Next
            Loader.Output = New DlOptiFineListResult With {.IsOfficial = False, .SourceName = "BMCLAPI", .Value = Versions}
        Catch ex As Exception
            Throw New Exception("版本列表解析失败（" & Json.ToString & "）", ex)
        End Try
    End Sub

#End Region

#Region "DlForgeList | Forge Minecraft 版本列表"

    Public Structure DlForgeListResult
        ''' <summary>
        ''' 数据来源名称，如“Official”，“BMCLAPI”。
        ''' </summary>
        Public SourceName As String
        ''' <summary>
        ''' 是否为官方的实时数据。
        ''' </summary>
        Public IsOfficial As Boolean
        ''' <summary>
        ''' 获取到的数据。
        ''' </summary>
        Public Value As List(Of String)
    End Structure

    ''' <summary>
    ''' Forge 版本列表，主加载器。
    ''' </summary>
    Public DlForgeListLoader As New LoaderTask(Of Integer, DlForgeListResult)("DlForgeList Main", AddressOf DlForgeListMain)
    Private Sub DlForgeListMain(Loader As LoaderTask(Of Integer, DlForgeListResult))
        Select Case Setup.Get("ToolDownloadVersion")
            Case 0
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)(DlForgeListBmclapiLoader, 30),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)(DlForgeListOfficialLoader, 60)
                }, Loader.IsForceRestarting)
            Case 1
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)(DlForgeListOfficialLoader, 5),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)(DlForgeListBmclapiLoader, 35)
                }, Loader.IsForceRestarting)
            Case Else
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)(DlForgeListOfficialLoader, 60),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlForgeListResult), Integer)(DlForgeListBmclapiLoader, 60)
                }, Loader.IsForceRestarting)
        End Select
    End Sub

    ''' <summary>
    ''' Forge 版本列表，官方源。
    ''' </summary>
    Public DlForgeListOfficialLoader As New LoaderTask(Of Integer, DlForgeListResult)("DlForgeList Official", AddressOf DlForgeListOfficialMain)
    Private Sub DlForgeListOfficialMain(Loader As LoaderTask(Of Integer, DlForgeListResult))
        Dim Result As String = NetGetCodeByRequestRetry("https://files.minecraftforge.net/maven/net/minecraftforge/forge/index_1.2.4.html", Encoding.Default, "text/html")
        If Result.Length < 200 Then Throw New Exception("获取到的版本列表长度不足（" & Result & "）")
        '获取所有版本信息
        Dim Names As List(Of String) = RegexSearch(Result, "(?<=a href=""index_)[0-9.]+(_pre[0-9]?)?(?=.html)")
        Names.Add("1.2.4") '1.2.4 不会被匹配上
        If Names.Count < 10 Then Throw New Exception("获取到的版本数量不足（" & Result & "）")
        Loader.Output = New DlForgeListResult With {.IsOfficial = True, .SourceName = "Forge 官方源", .Value = Names}
    End Sub

    ''' <summary>
    ''' Forge 版本列表，BMCLAPI。
    ''' </summary>
    Public DlForgeListBmclapiLoader As New LoaderTask(Of Integer, DlForgeListResult)("DlForgeList Bmclapi", AddressOf DlForgeListBmclapiMain)
    Private Sub DlForgeListBmclapiMain(Loader As LoaderTask(Of Integer, DlForgeListResult))
        Dim Result As String = NetGetCodeByRequestRetry("https://bmclapi2.bangbang93.com/forge/minecraft", Encoding.Default,
                                                       BackupUrl:="https://download.mcbbs.net/forge/minecraft")
        If Result.Length < 200 Then Throw New Exception("获取到的版本列表长度不足（" & Result & "）")
        '获取所有版本信息
        Dim Names As List(Of String) = RegexSearch(Result, "[0-9.]+(_pre[0-9]?)?")
        If Names.Count < 10 Then Throw New Exception("获取到的版本数量不足（" & Result & "）")
        Loader.Output = New DlForgeListResult With {.IsOfficial = False, .SourceName = "BMCLAPI", .Value = Names}
    End Sub

#End Region

#Region "DlForgeVersion | Forge 版本列表"

    Public Class DlForgeVersionEntry
        ''' <summary>
        ''' 完整的版本名，如 “14.22.1.2478”。
        ''' </summary>
        Public Version As String
        ''' <summary>
        ''' 对应的 Minecraft 版本，如“1.12.2”。
        ''' </summary>
        Public Inherit As String
        ''' <summary>
        ''' 发布时间，格式为“yyyy/MM/dd HH:mm”。
        ''' </summary>
        Public ReleaseTime As String
        ''' <summary>
        ''' 文件的 MD5 或 SHA1（BMCLAPI 的老版本是 MD5，新版本是 SHA1；官方源总是 MD5）。
        ''' </summary>
        Public Hash As String = Nothing
        ''' <summary>
        ''' 是否为推荐版本。
        ''' </summary>
        Public IsRecommended As Boolean
        ''' <summary>
        ''' 安装类型。有 installer、client、universal 三种。
        ''' </summary>
        Public Category As String
        ''' <summary>
        ''' 版本分支。若无分支则为 Nothing。
        ''' </summary>
        Public Branch As String = Nothing
        ''' <summary>
        ''' 是否为新版 Forge。（即为 Minecraft 1.13+）
        ''' </summary>
        Public ReadOnly Property IsNewType As Boolean
            Get
                Return Version.Split(".")(0) >= 20
            End Get
        End Property
        ''' <summary>
        ''' 构建数。
        ''' </summary>
        Public ReadOnly Property Build As Integer
            Get
                Dim Version = Me.Version.Split(".")
                If Version(0) < 15 Then
                    Return Version(Version.Count - 1)
                Else
                    Return Version(Version.Count - 1) + 10000
                End If
            End Get
        End Property
        ''' <summary>
        ''' 用于下载的文件版本名。可能在 Version 的基础上添加了分支。
        ''' </summary>
        Public ReadOnly Property FileVersion As String
            Get
                Return Version & If(Branch Is Nothing, ""， "-" & Branch)
            End Get
        End Property
        ''' <summary>
        ''' 即将下载的文件全名。
        ''' </summary>
        Public ReadOnly Property FileName As String
            Get
                Return "forge-" & Inherit & "-" & FileVersion & "-" & Category & "." & FileSuffix
            End Get
        End Property
        ''' <summary>
        ''' 文件扩展名。
        ''' </summary>
        Public ReadOnly Property FileSuffix As String
            Get
                If Category = "installer" Then
                    Return "jar"
                Else
                    Return "zip"
                End If
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Forge 版本列表，主加载器。
    ''' </summary>
    Public Sub DlForgeVersionMain(Loader As LoaderTask(Of String, List(Of DlForgeVersionEntry)))
        Dim DlForgeVersionOfficialLoader As New LoaderTask(Of String, List(Of DlForgeVersionEntry))("DlForgeVersion Official", AddressOf DlForgeVersionOfficialMain)
        Dim DlForgeVersionBmclapiLoader As New LoaderTask(Of String, List(Of DlForgeVersionEntry))("DlForgeVersion Bmclapi", AddressOf DlForgeVersionBmclapiMain)
        Select Case Setup.Get("ToolDownloadVersion")
            Case 0
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)(DlForgeVersionBmclapiLoader, 30),
                    New KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)(DlForgeVersionOfficialLoader, 60)
                }, Loader.IsForceRestarting)
            Case 1
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)(DlForgeVersionOfficialLoader, 5),
                    New KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)(DlForgeVersionBmclapiLoader, 35)
                }, Loader.IsForceRestarting)
            Case Else
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)(DlForgeVersionOfficialLoader, 60),
                    New KeyValuePair(Of LoaderTask(Of String, List(Of DlForgeVersionEntry)), Integer)(DlForgeVersionBmclapiLoader, 60)
                }, Loader.IsForceRestarting)
        End Select
    End Sub

    ''' <summary>
    ''' Forge 版本列表，官方源。
    ''' </summary>
    Public Sub DlForgeVersionOfficialMain(Loader As LoaderTask(Of String, List(Of DlForgeVersionEntry)))
        Dim Result As String
        Try
            Result = NetGetCodeByDownload("https://files.minecraftforge.net/maven/net/minecraftforge/forge/index_" & Loader.Input & ".html")
        Catch ex As Exception
            If GetExceptionSummary(ex).Contains("(404)") Then
                Throw New Exception("没有可用版本")
            Else
                Throw
            End If
        End Try
        If Result.Length < 1000 Then Throw New Exception("获取到的版本列表长度不足（" & Result & "）")
        Dim Versions As New List(Of DlForgeVersionEntry)
        Try
            '分割版本信息
            Dim VersionCodes = Mid(Result, 1, Result.LastIndexOf("</table>")).Split("<td class=""download-version")
            '获取所有版本信息
            For i = 1 To VersionCodes.Count - 1
                Dim VersionCode = VersionCodes(i)
                Try
                    '基础信息获取
                    Dim Name As String = RegexSeek(VersionCode, "(?<=[^(0-9)]+)[0-9\.]+")
                    Dim IsRecommended As Boolean = VersionCode.Contains("fa promo-recommended")
                    Dim Inherit As String = Loader.Input
                    '分支获取
                    Dim Branch As String = RegexSeek(VersionCode, $"(?<=-{Name}-)[^-""]+(?=-[a-z]+.[a-z]{{3}})")
                    If String.IsNullOrWhiteSpace(Branch) Then Branch = Nothing
                    If Name = "11.15.1.2318" OrElse Name = "11.15.1.1902" OrElse Name = "11.15.1.1890" Then Branch = "1.8.9"
                    If Branch Is Nothing AndAlso Inherit = "1.7.10" AndAlso Name.Split(".")(3) >= 1300 Then Branch = "1.7.10"
                    '发布时间获取
                    Dim ReleaseTimeOriginal = RegexSeek(VersionCode, "(?<=""download-time"" title="")[^""]+")
                    Dim ReleaseTimeSplit = ReleaseTimeOriginal.Split(" -:".ToCharArray) '原格式："2021-02-15 03:24:02"
                    Dim ReleaseDate As New Date(ReleaseTimeSplit(0), ReleaseTimeSplit(1), ReleaseTimeSplit(2), '年月日
                                                ReleaseTimeSplit(3), ReleaseTimeSplit(4), ReleaseTimeSplit(5), '时分秒
                                                0, DateTimeKind.Utc) '以 UTC 时间作为标准
                    Dim ReleaseTime As String = ReleaseDate.ToLocalTime.ToString("yyyy/MM/dd HH:mm") '时区与格式转换
                    '分类与 MD5 获取
                    Dim MD5 As String, Category As String
                    If VersionCode.Contains("classifier-installer""") Then
                        '类型为 installer.jar，支持范围 ~753 (~ 1.6.1 部分), 738~684 (1.5.2 全部)
                        VersionCode = VersionCode.Substring(VersionCode.IndexOf("installer.jar"))
                        MD5 = RegexSeek(VersionCode, "(?<=MD5:</strong> )[^<]+")
                        Category = "installer"
                    ElseIf VersionCode.Contains("classifier-universal""") Then
                        '类型为 universal.zip，支持范围 751~449 (1.6.1 部分), 682~183 (1.5.1 ~ 1.3.2 部分)
                        VersionCode = VersionCode.Substring(VersionCode.IndexOf("universal.zip"))
                        MD5 = RegexSeek(VersionCode, "(?<=MD5:</strong> )[^<]+")
                        Category = "universal"
                    ElseIf VersionCode.Contains("client.zip") Then
                        '类型为 client.zip，支持范围 182~ (1.3.2 部分 ~)
                        VersionCode = VersionCode.Substring(VersionCode.IndexOf("client.zip"))
                        MD5 = RegexSeek(VersionCode, "(?<=MD5:</strong> )[^<]+")
                        Category = "client"
                    Else
                        '没有任何下载（1.6.4 有一部分这种情况）
                        Continue For
                    End If
                    '添加进列表
                    Versions.Add(New DlForgeVersionEntry With {.Category = Category, .Version = Name, .IsRecommended = IsRecommended, .Hash = MD5.Trim(vbCr, vbLf), .Inherit = Inherit, .ReleaseTime = ReleaseTime, .Branch = Branch})
                Catch ex As Exception
                    Throw New Exception("版本信息提取失败（" & VersionCode & "）", ex)
                End Try
            Next
        Catch ex As Exception
            Throw New Exception("版本列表解析失败（" & Result & "）", ex)
        End Try
        If Versions.Count = 0 Then Throw New Exception("没有可用版本")
        Loader.Output = Versions
    End Sub

    ''' <summary>
    ''' Forge 版本列表，BMCLAPI。
    ''' </summary>
    Public Sub DlForgeVersionBmclapiMain(Loader As LoaderTask(Of String, List(Of DlForgeVersionEntry)))
        Dim Json As JArray = NetGetCodeByRequestRetry("https://bmclapi2.bangbang93.com/forge/minecraft/" & Loader.Input,
                                                       BackupUrl:="https://download.mcbbs.net/forge/minecraft/" & Loader.Input, IsJson:=True)
        Dim Versions As New List(Of DlForgeVersionEntry)
        Try
            Dim Recommended As String = McDownloadForgeRecommendedGet(Loader.Input)
            For Each Token As JObject In Json
                '分类与 Hash 获取
                Dim Hash As String = Nothing, Category As String = "unknown", Proi As Integer = -1
                For Each File As JObject In Token("files")
                    Select Case File("category").ToString
                        Case "installer"
                            If File("format").ToString = "jar" Then
                                '类型为 installer.jar，支持范围 ~753 (~ 1.6.1 部分), 738~684 (1.5.2 全部)
                                Hash = File("hash")
                                Category = "installer"
                                Proi = 2
                            End If
                        Case "universal"
                            If Proi <= 1 AndAlso File("format").ToString = "zip" Then
                                '类型为 universal.zip，支持范围 751~449 (1.6.1 部分), 682~183 (1.5.1 ~ 1.3.2 部分)
                                Hash = File("hash")
                                Category = "universal"
                                Proi = 1
                            End If
                        Case "client"
                            If Proi <= 0 AndAlso File("format").ToString = "zip" Then
                                '类型为 client.zip，支持范围 182~ (1.3.2 部分 ~)
                                Hash = File("hash")
                                Category = "client"
                                Proi = 0
                            End If
                    End Select
                Next
                '获取 Entry
                Dim Inherit As String = Loader.Input
                Dim Branch As String = Token("branch")
                Dim Name As String = Token("version")
                If Name = "11.15.1.2318" OrElse Name = "11.15.1.1902" OrElse Name = "11.15.1.1890" Then Branch = "1.8.9"
                If Branch Is Nothing AndAlso Inherit = "1.7.10" AndAlso Name.Split(".")(3) >= 1300 Then Branch = "1.7.10"
                '基础信息获取
                Dim Entry = New DlForgeVersionEntry With {.Hash = Hash, .Category = Category, .Version = Name, .Branch = Branch, .Inherit = Inherit, .IsRecommended = Recommended = Name}
                Dim TimeSplit = Token("modified").ToString.Split("-"c, "T"c, ":"c, "."c, " "c, "/"c)
                Entry.ReleaseTime = Token("modified").ToObject(Of Date).ToLocalTime.ToString("yyyy/MM/dd HH:mm")
                '添加项
                Versions.Add(Entry)
            Next
        Catch ex As Exception
            Throw New Exception("版本列表解析失败（" & Json.ToString & "）", ex)
        End Try
        If Versions.Count = 0 Then Throw New Exception("没有可用版本")
        Loader.Output = Versions
    End Sub

#End Region

#Region "DlLiteLoaderList | LiteLoader 版本列表"

    Public Structure DlLiteLoaderListResult
        ''' <summary>
        ''' 数据来源名称，如“Official”，“BMCLAPI”。
        ''' </summary>
        Public SourceName As String
        ''' <summary>
        ''' 是否为官方的实时数据。
        ''' </summary>
        Public IsOfficial As Boolean
        ''' <summary>
        ''' 获取到的数据。
        ''' </summary>
        Public Value As List(Of DlLiteLoaderListEntry)
        ''' <summary>
        ''' 官方源的失败原因。若没有则为 Nothing。
        ''' </summary>
        Public OfficialError As Exception
    End Structure
    Public Class DlLiteLoaderListEntry
        ''' <summary>
        ''' 实际的文件名，如“liteloader-installer-1.12-00-SNAPSHOT.jar”。
        ''' </summary>
        Public FileName As String
        ''' <summary>
        ''' 是否为测试版。
        ''' </summary>
        Public IsPreview As Boolean
        ''' <summary>
        ''' 对应的 Minecraft 版本，如“1.12.2”。
        ''' </summary>
        Public Inherit As String
        ''' <summary>
        ''' 是否为 1.7 及更早的远古版。
        ''' </summary>
        Public IsLegacy As Boolean
        ''' <summary>
        ''' 发布时间，格式为“yyyy/mm/dd HH:mm”。
        ''' </summary>
        Public ReleaseTime As String
        ''' <summary>
        ''' 文件的 MD5。
        ''' </summary>
        Public MD5 As String
        ''' <summary>
        ''' 对应的 Json 项。
        ''' </summary>
        Public JsonToken As JToken
    End Class

    ''' <summary>
    ''' LiteLoader 版本列表，主加载器。
    ''' </summary>
    Public DlLiteLoaderListLoader As New LoaderTask(Of Integer, DlLiteLoaderListResult)("DlLiteLoaderList Main", AddressOf DlLiteLoaderListMain)
    Private Sub DlLiteLoaderListMain(Loader As LoaderTask(Of Integer, DlLiteLoaderListResult))
        Select Case Setup.Get("ToolDownloadVersion")
            Case 0
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)(DlLiteLoaderListBmclapiLoader, 30),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)(DlLiteLoaderListOfficialLoader, 60)
                }, Loader.IsForceRestarting)
            Case 1
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)(DlLiteLoaderListOfficialLoader, 5),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)(DlLiteLoaderListBmclapiLoader, 35)
                }, Loader.IsForceRestarting)
            Case Else
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)(DlLiteLoaderListOfficialLoader, 60),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlLiteLoaderListResult), Integer)(DlLiteLoaderListBmclapiLoader, 60)
                }, Loader.IsForceRestarting)
        End Select
    End Sub

    ''' <summary>
    ''' LiteLoader 版本列表，官方源。
    ''' </summary>
    Public DlLiteLoaderListOfficialLoader As New LoaderTask(Of Integer, DlLiteLoaderListResult)("DlLiteLoaderList Official", AddressOf DlLiteLoaderListOfficialMain)
    Private Sub DlLiteLoaderListOfficialMain(Loader As LoaderTask(Of Integer, DlLiteLoaderListResult))
        Dim Result As JObject = NetGetCodeByRequestRetry("https://dl.liteloader.com/versions/versions.json", IsJson:=True)
        Try
            Dim Json As JObject = Result("versions")
            Dim Versions As New List(Of DlLiteLoaderListEntry)
            For Each Pair As KeyValuePair(Of String, JToken) In Json
                If Pair.Key.StartsWith("1.6") OrElse Pair.Key.StartsWith("1.5") Then Continue For
                Dim RealEntry As JToken = If(Pair.Value("artefacts"), Pair.Value("snapshots"))("com.mumfrey:liteloader")("latest")
                Versions.Add(New DlLiteLoaderListEntry With {
                             .Inherit = Pair.Key,
                             .IsLegacy = Pair.Key.Split(".")(1) < 8,
                             .IsPreview = RealEntry("stream").ToString.ToLower = "snapshot",
                             .FileName = "liteloader-installer-" & Pair.Key & If(Pair.Key = "1.8" OrElse Pair.Key = "1.9", ".0", "") & "-00-SNAPSHOT.jar",
                             .MD5 = RealEntry("md5"),
                             .ReleaseTime = GetLocalTime(GetDate(RealEntry("timestamp"))).ToString("yyyy/MM/dd HH:mm"),
                             .JsonToken = RealEntry
                         })
            Next
            Loader.Output = New DlLiteLoaderListResult With {.IsOfficial = True, .SourceName = "LiteLoader 官方源", .Value = Versions}
        Catch ex As Exception
            Throw New Exception("版本列表解析失败（" & Result.ToString & "）", ex)
        End Try
    End Sub

    ''' <summary>
    ''' LiteLoader 版本列表，BMCLAPI。
    ''' </summary>
    Public DlLiteLoaderListBmclapiLoader As New LoaderTask(Of Integer, DlLiteLoaderListResult)("DlLiteLoaderList Bmclapi", AddressOf DlLiteLoaderListBmclapiMain)
    Private Sub DlLiteLoaderListBmclapiMain(Loader As LoaderTask(Of Integer, DlLiteLoaderListResult))
        Dim Result As JObject = NetGetCodeByRequestRetry("https://bmclapi2.bangbang93.com/maven/com/mumfrey/liteloader/versions.json",
                                                       BackupUrl:="https://download.mcbbs.net/maven/com/mumfrey/liteloader/versions.json", IsJson:=True)
        Try
            Dim Json As JObject = Result("versions")
            Dim Versions As New List(Of DlLiteLoaderListEntry)
            For Each Pair As KeyValuePair(Of String, JToken) In Json
                If Pair.Key.StartsWith("1.6") OrElse Pair.Key.StartsWith("1.5") Then Continue For
                Dim RealEntry As JToken = If(Pair.Value("artefacts"), Pair.Value("snapshots"))("com.mumfrey:liteloader")("latest")
                Versions.Add(New DlLiteLoaderListEntry With {
                             .Inherit = Pair.Key,
                             .IsLegacy = Pair.Key.Split(".")(1) < 8,
                             .IsPreview = RealEntry("stream").ToString.ToLower = "snapshot",
                             .FileName = "liteloader-installer-" & Pair.Key & If(Pair.Key = "1.8" OrElse Pair.Key = "1.9", ".0", "") & "-00-SNAPSHOT.jar",
                             .MD5 = RealEntry("md5"),
                             .ReleaseTime = GetLocalTime(GetDate(RealEntry("timestamp"))).ToString("yyyy/MM/dd HH:mm"),
                             .JsonToken = RealEntry
                         })
            Next
            Loader.Output = New DlLiteLoaderListResult With {.IsOfficial = False, .SourceName = "BMCLAPI", .Value = Versions}
        Catch ex As Exception
            Throw New Exception("版本列表解析失败（" & Result.ToString & "）", ex)
        End Try
    End Sub

#End Region

#Region "DlFabricList | Fabric 列表"

    Public Structure DlFabricListResult
        ''' <summary>
        ''' 数据来源名称，如“Official”，“BMCLAPI”。
        ''' </summary>
        Public SourceName As String
        ''' <summary>
        ''' 是否为官方的实时数据。
        ''' </summary>
        Public IsOfficial As Boolean
        ''' <summary>
        ''' 获取到的数据。
        ''' </summary>
        Public Value As JObject
    End Structure

    ''' <summary>
    ''' Fabric 列表，主加载器。
    ''' </summary>
    Public DlFabricListLoader As New LoaderTask(Of Integer, DlFabricListResult)("DlFabricList Main", AddressOf DlFabricListMain)
    Private Sub DlFabricListMain(Loader As LoaderTask(Of Integer, DlFabricListResult))
        Select Case Setup.Get("ToolDownloadVersion")
            Case 0
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)(DlFabricListBmclapiLoader, 30),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)(DlFabricListOfficialLoader, 60)
                }, Loader.IsForceRestarting)
            Case 1
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)(DlFabricListOfficialLoader, 5),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)(DlFabricListBmclapiLoader, 35)
                }, Loader.IsForceRestarting)
            Case Else
                DlSourceLoader(Loader, New List(Of KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)) From {
                    New KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)(DlFabricListOfficialLoader, 60),
                    New KeyValuePair(Of LoaderTask(Of Integer, DlFabricListResult), Integer)(DlFabricListBmclapiLoader, 60)
                }, Loader.IsForceRestarting)
        End Select
    End Sub

    ''' <summary>
    ''' Fabric 列表，官方源。
    ''' </summary>
    Public DlFabricListOfficialLoader As New LoaderTask(Of Integer, DlFabricListResult)("DlFabricList Official", AddressOf DlFabricListOfficialMain)
    Private Sub DlFabricListOfficialMain(Loader As LoaderTask(Of Integer, DlFabricListResult))
        Dim Result As JObject = NetGetCodeByRequestRetry("https://meta.fabricmc.net/v2/versions", IsJson:=True)
        Try
            Dim Output = New DlFabricListResult With {.IsOfficial = True, .SourceName = "Fabric 官方源", .Value = Result}
            If Output.Value("game") Is Nothing OrElse Output.Value("loader") Is Nothing OrElse Output.Value("installer") Is Nothing Then Throw New Exception("获取到的列表缺乏必要项")
            Loader.Output = Output
        Catch ex As Exception
            Throw New Exception("列表解析失败（" & Result.ToString & "）", ex)
        End Try
    End Sub

    ''' <summary>
    ''' Fabric 列表，BMCLAPI。
    ''' </summary>
    Public DlFabricListBmclapiLoader As New LoaderTask(Of Integer, DlFabricListResult)("DlFabricList Bmclapi", AddressOf DlFabricListBmclapiMain)
    Private Sub DlFabricListBmclapiMain(Loader As LoaderTask(Of Integer, DlFabricListResult))
        Dim Result As JObject = NetGetCodeByRequestRetry("https://bmclapi2.bangbang93.com/fabric-meta/v2/versions",
                                                       BackupUrl:="https://download.mcbbs.net/fabric-meta/v2/versions", IsJson:=True)
        Try
            Dim Output = New DlFabricListResult With {.IsOfficial = False, .SourceName = "BMCLAPI", .Value = Result}
            If Output.Value("game") Is Nothing OrElse Output.Value("loader") Is Nothing OrElse Output.Value("installer") Is Nothing Then Throw New Exception("获取到的列表缺乏必要项")
            Loader.Output = Output
        Catch ex As Exception
            Throw New Exception("列表解析失败（" & Result.ToString & "）", ex)
        End Try
    End Sub

    ''' <summary>
    ''' Fabric API 列表，官方源。
    ''' </summary>
    Public DlFabricApiLoader As New LoaderTask(Of Integer, List(Of CompFile))("Fabric API List Loader",
        Sub(Task As LoaderTask(Of Integer, List(Of CompFile))) Task.Output = CompFilesGet("fabric-api", False))

    ''' <summary>
    ''' OptiFabric 列表，官方源。
    ''' </summary>
    Public DlOptiFabricLoader As New LoaderTask(Of Integer, List(Of CompFile))("OptiFabric List Loader",
        Sub(Task As LoaderTask(Of Integer, List(Of CompFile))) Task.Output = CompFilesGet("322385", True))

#End Region

#Region "DlSource | 镜像下载源"

    Public Function DlSourceResourceGet(MojangBase As String) As String()
        MojangBase = MojangBase.Replace("http://resources.download.minecraft.net", "https://resources.download.minecraft.net")
        Return {MojangBase.Replace("https://piston-data.mojang.com", "https://download.mcbbs.net/assets").Replace("https://piston-meta.mojang.com", "https://download.mcbbs.net/assets").Replace("https://resources.download.minecraft.net", "https://download.mcbbs.net/assets"),
                MojangBase,
                MojangBase.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com/assets").Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com/assets").Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets")
               }
    End Function

    Public Function DlSourceLibraryGet(MojangBase As String) As String()
        Return {MojangBase.Replace("https://piston-data.mojang.com", "https://download.mcbbs.net/maven").Replace("https://piston-meta.mojang.com", "https://download.mcbbs.net/maven").Replace("https://libraries.minecraft.net", "https://download.mcbbs.net/maven"),
                MojangBase.Replace("https://piston-data.mojang.com", "https://download.mcbbs.net/libraries").Replace("https://piston-meta.mojang.com", "https://download.mcbbs.net/libraries").Replace("https://libraries.minecraft.net", "https://download.mcbbs.net/libraries"),
                MojangBase,
                MojangBase.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com/maven").Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com/maven").Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven"),
                MojangBase.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com/libraries").Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com/libraries").Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/libraries")
               }
    End Function

    Public Function DlSourceLauncherOrMetaGet(MojangBase As String) As String()
        If MojangBase Is Nothing Then Throw New Exception("无对应的 json 下载地址")
        Return {MojangBase.Replace("https://piston-data.mojang.com", "https://download.mcbbs.net").Replace("https://piston-meta.mojang.com", "https://download.mcbbs.net").Replace("https://launcher.mojang.com", "https://download.mcbbs.net").Replace("https://launchermeta.mojang.com", "https://download.mcbbs.net"),
                MojangBase,
                MojangBase.Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com").Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com").Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com").Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com")
               }
    End Function

    Private Sub DlSourceLoader(Of InputType, OutputType)(MainLoader As LoaderTask(Of InputType, OutputType),
                                                         LoaderList As List(Of KeyValuePair(Of LoaderTask(Of InputType, OutputType), Integer)),
                                                         Optional IsForceRestart As Boolean = False)
        Dim WaitCycle As Integer = 0
        Do While True
            '检查加载结束
            Dim IsAllFailed As Boolean = True
            For Each SubLoader In LoaderList
                If WaitCycle = 0 Then
                    '如果要强制刷新，就不使用已经加载好的值
                    If IsForceRestart Then Exit For
                    '如果输入不一样，就不使用已经加载好的值
                    If (SubLoader.Key.Input Is Nothing Xor MainLoader.Input Is Nothing) OrElse
                       (SubLoader.Key.Input IsNot Nothing AndAlso Not SubLoader.Key.Input.Equals(MainLoader.Input)) Then Continue For
                End If
                If SubLoader.Key.State <> LoadState.Failed Then IsAllFailed = False
                If SubLoader.Key.State = LoadState.Finished Then
                    MainLoader.Output = SubLoader.Key.Output
                    DlSourceLoaderAbort(LoaderList)
                    Exit Sub
                ElseIf IsAllFailed Then
                    If WaitCycle < SubLoader.Value * 100 Then WaitCycle = SubLoader.Value * 100
                End If
                '由于 Forge BMCLAPI 没有可用版本导致强制失败
                '在没有可用版本时，官方源会一直卡住，直接使用 BMCLAPI 判定失败即可
                If SubLoader.Key.Error IsNot Nothing AndAlso SubLoader.Key.Error.Message.Contains("没有可用版本") Then
                    For Each SubLoader2 In LoaderList
                        If WaitCycle < SubLoader2.Value * 100 Then WaitCycle = SubLoader2.Value * 100
                    Next
                End If
            Next
            '启动加载源
            If WaitCycle = 0 Then
                '启动第一个源
                LoaderList(0).Key.Start(MainLoader.Input, IsForceRestart)
                '将其他源标记为未启动，以确保可以切换下载源（#184）
                For i = 1 To LoaderList.Count - 1
                    LoaderList(i).Key.State = LoadState.Waiting
                Next
            End If
            For i = 0 To LoaderList.Count - 1
                If WaitCycle = LoaderList(i).Value * 100 Then
                    If i < LoaderList.Count - 1 Then
                        '启动下一个源
                        LoaderList(i + 1).Key.Start(MainLoader.Input, IsForceRestart)
                    Else
                        '失败
                        Dim ErrorInfo As Exception = Nothing
                        For ii = 0 To LoaderList.Count - 1
                            LoaderList(ii).Key.Input = Nothing '重置输入，以免以同样的输入“重试加载”时直接失败
                            If LoaderList(ii).Key.Error IsNot Nothing Then
                                If ErrorInfo Is Nothing OrElse LoaderList(ii).Key.Error.Message.Contains("没有可用版本") Then
                                    ErrorInfo = LoaderList(ii).Key.Error
                                End If
                            End If
                        Next
                        If ErrorInfo Is Nothing Then ErrorInfo = New TimeoutException("下载源连接超时")
                        DlSourceLoaderAbort(LoaderList)
                        Throw ErrorInfo
                    End If
                    Exit For
                End If
            Next
            '计时
            If MainLoader.IsAborted Then
                DlSourceLoaderAbort(LoaderList)
                Exit Sub
            End If
            Thread.Sleep(10)
            WaitCycle += 1
        Loop
    End Sub
    Private Sub DlSourceLoaderAbort(Of InputType, OutputType)(LoaderList As List(Of KeyValuePair(Of LoaderTask(Of InputType, OutputType), Integer)))
        For Each Loader In LoaderList
            If Loader.Key.State = LoadState.Loading Then Loader.Key.Abort()
        Next
    End Sub

#End Region

End Module
