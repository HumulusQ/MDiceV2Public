# MDiceV2 指令與解析規則

## 通用識別規則
- 觸發前綴：所有常規指令均以 `.` 開頭，支持的前綴依次為：bot、r、st、sc、cc、log、rule、dismiss、help、name、com、duel（來源：MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L48-L86）。
- 大小寫：指令匹配不區分大小寫，會先 `Trim()` 後判斷。
- Bot 開關：除 `.bot` 外，其他指令在當前會話被關閉時會被忽略，`.bot` 可用來查詢/開關（MDiceV2.Core/Models/MessageProcessor.cs#L1349-L1385）。
- 管理員前綴：以 `#` 開頭的指令走獨立通道，需要 Master / 1001 / 個人白名單等級 < 3（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L120-L194）。
- 日誌：群聊且已開啟日誌時會記錄清洗後的消息文本（去掉 CQ 圖片/錄音等標記）。

## 管理員指令
### #aa —— 設置白名單等級
- 權限：1001、Master、個人白名單等級 < 3 才可用。
- 解析：
  ```
  ^#aa\s*(?<mode>[gpGP])?\s*(?<id>\S+)\s+(?<level>\d)
  ```
  - `mode`：g=群，p=個人，默認 p。
  - `id`：從原文或 CQ 碼中提取第一段數字（`qq=123` 或任意數字串）。
  - `level`：單個數字 0-9。
- 功能：設置對應群/用戶的白名單等級並持久化（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L196-L252）。

## 常規指令
### .r —— 掷骰
- 語法：`.r` 或 `.r <表達式>`。
- 行為：無參默認 1d100；有參將整段作為擲骰表達式交給 `Dice.CalculateExpression`，失敗返回錯誤明細（MDiceV2.Core/Models/MessageProcessor.cs#L1324-L1347）。

### .bot —— 機器人開關/狀態
- 語法：`.bot`（查詢）、`.bot on`、`.bot off`。
- 範圍：群聊按群 ID，私聊按用戶 ID（用戶鍵為負數）。
- 行為：開/關並持久化；查詢時回傳當前狀態及信任值（MDiceV2.Core/Models/MessageProcessor.cs#L1349-L1385）。

### .log —— 跑團日誌管理（僅群聊）
- 指令別名：`on|off|get|review` 可直接緊貼在 `.log` 後或以空格分隔，後綴會自動拆分。
- 參數：`<名稱>` 可包含空格，會被整體 `Trim()`。
- 功能：
  - `on <名稱>`：開啟並開始新日誌；要求名稱非空。
  - `off <名稱>`：僅開啟者或群管理可關閉。
  - `get <名稱>`：上傳對應 HTML 日誌文件到群文件。
  - `review <名稱>`：轉發最後 20 條日誌記錄。
- 位置：MDiceV2.Core/Models/MessageProcessor.cs#L1387-L1518。

### .rule —— 查詢規則書條目
- 解析：可選規則書名放在括號中，正則 `^\((.*?)\)\s*(.*)$`。
- 語法：`.rule(規則書)鍵` 或 `.rule 鍵`。
- 行為：
  - 未指定規則書時，優先使用用戶上次的規則書，否則默認 `default_rule`。
  - 找到條目時返回 `規則書 鍵: 值`，並在指定時記錄當前規則書。
  - 未找到時提示缺失（MDiceV2.Core/Models/MessageProcessor.cs#L1521-L1622）。

### .dismiss —— 退群
- 僅群聊；調用後機器人退群（MDiceV2.Core/Models/MessageProcessor.cs#L1624-L1640）。

### .st —— 人物卡錄入/配置
- 入口正則：
  ```
  ^\.st\s*(?:\((?<character>[^)]+)\))?\s*(?<tail>.*)$
  ```
  - 可選 `(<人物名>)`；`tail` 為後續文本（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L258-L300）。
- 配置塊：從尾部開始解析若干 `{key:value}`，鍵僅支持 `type`、`cocformat`，按出現順序消費（L312-L357）。
- 技能段解析：
  - 技能名：連續非數字字符，若含數字則跳過。
  - 值：整數或骰式 `^\d*[dD]\d*$`；可在值前放 `+`/`-` 代表相對調整，否則覆蓋。
  - 數值超界會提示並限制在 0~9999。
  - 新建人物卡上限 6 張；成功後持久化（L359-L472）。

### .cc —— 自定義檢定（CoC7 / ET）
- 入口正則：
  ```
  ^\.cc(?:\{(?<mode>[^}]+)\})?(?:\((?<character>[^)]+)\))?\s*(?<mainPart>.*?)(?:\s*(?<subCommand>-\w+))?$ 
  ```
  - `mode` 默認 coc7，可選 `coc7` 或 `et`。
  - `character` 可選；`mainPart` 為主體；`subCommand` 可選如 `-l`（循環處理全部主體）（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L477-L589）。
- 主體拆分：`ParseMainPartSimple` 使用正則
  ```
  (?<subCmd>\.(?:p|b|v|a|d)[\+\-]?\d*|\.s\d*|\.r|\.\#\d?)|(?<skill>\b\w+(?:[\+\-]\d+)?\b)|(?<value>\b\d+\b)
  ```
  由左到右組合：子命令 + 技能/值，未匹配時仍會組裝第一個可用元素（MDiceV2.Core/Models/MessageProcessor_Utils.cs#L17-L70）。
- CoC7 分支：
  - 技能標記格式 `名稱[±修正]`，從人物卡讀值並加修正；僅填數字時直接作檢定值；空時沿用上一條技能/子命令。
  - 擲 `1d100`，結果用 `Dice.CoC7_Check` 評級；子命令 `.p/.b/.v/.a/.d/.s` 標記會被記錄但僅簡化處理（MDiceV2.Core/Models/MessageProcessor_Utils.cs#L83-L141）。
- ET 分支：
  - 同樣解析技能/修正；擲 `1d20`。
  - 結果規則：`roll=1` 大失敗，`roll=技能值` 大成功，`roll<技能值` 成功，否則拙劣；檢定數值=技能值/2，成功類型時再加擲骰（L165-L223）。
- `-l` 副指令：在有多段主體時逐段處理；默認僅首段。

### .sc —— 理智檢定
- 正則：
  ```
  ^\.sc(?:\(([^)]+)\))?\s*([^/\s]+)\s*/\s*([^/\s]+)(?:\s+(\d+))?$
  ```
  - 可選人物名；兩段骰表達式以 `/` 分隔；可選臨時理智值（數字）（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L591-L704）。
- 流程：
  - 讀取人物卡的「理智」，若缺失則用「意志」並回寫到理智。
  - 擲 `1d100` 對比當前理智（臨時值優先生效）；成功用首表達式計算損失，失敗用第二表達式。
  - 更新並保存理智值，回覆檢定結果與剩餘理智。

### .help —— 幫助列表/查詢
- `.help list [頁碼]`：每頁 20 條關鍵詞，頁碼缺省為 1，頁碼無效時會被糾正到範圍內。
- `.help <關鍵詞>`：精確查找；缺省時同列表第 1 頁（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L706-L765）。

### .name —— 設置/查詢顯示名
- `.name`：查詢當前名稱。
- `.name reset|clear|off`：清除名稱。
- `.name <文本>`：設置名稱，長度上限 32（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L766-L824）。

### .com —— 模式標記占位
- 語法：`.com [coc|et|dnd]`；缺省時返回當前人物卡詳情。
- 要求：需已有人物卡，否則提示創建；目前僅記錄意圖並回傳人物卡資訊（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L827-L871）。

### .duel —— 對戰遊戲入口
- 權限：1001、Master、個人白名單<3、群白名單<3；未滿足則拒絕（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L101-L118, L873-L915）。
- 語法：`.duel`；參數可填 `restart` 以重置存檔。
- 流程概要：
  - 每日回合有上限（白名單跳過）；未達上限則扣除娛樂好感度。
  - 新局或結束局會初始化卡組並同步規則說明、當前場面。
  - 當前回合若有待決策卡牌/手牌，會進入決策模式並設置聚焦，提示格式：
    - 角色卡：回覆 `1/2/3` 選擇前/中/後場。
    - 特殊卡：回覆 `y/n` 使用與否；或手牌模式下 `手牌序號.位置`，例如 `1.1`（第 1 張放前場）、`2.y`（使用第 2 張特殊卡）、`3.n`（放棄第 3 張），`0` 跳過。
  - 其他情況回覆當前回合抽牌/狀態，並計數回合（MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs#L873-L1197）。

## 補充
- 內容清洗：消息會替換 CQ 圖片為 `[圖片]`、語音為 `[語音]`、視頻為 `[視頻]` 後再記錄（MDiceV2.Core/Models/MessageProcessor.cs#L1642-L1657）。
- 人物卡持久化與查詢由其它輔助方法處理，命令條件參考上述行為即可。
