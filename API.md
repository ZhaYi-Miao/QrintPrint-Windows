# QrintPrint 远程打印 API

QrintPrint 内置一个局域网远程打印服务，可通过 HTTP 接口发起打印。覆盖桌面端全部打印能力：文本、图片、Markdown、条码、Word 文档、PDF、表格、课程表。所有接口与桌面端使用同一套渲染与打印链路。

## 目录

- [启用服务](#启用服务)
- [鉴权与多 Key 权限](#鉴权与多-key-权限)
- [通用约定](#通用约定)
- [接口列表](#接口列表)
  - [GET /api/health](#get-apihealth-健康检查)
  - [GET /api/status](#get-apistatus-查询打印机状态)
  - [POST /api/print/text](#post-apiprinttext-文本打印)
  - [POST /api/print/image](#post-apiprintimage-图片打印)
  - [POST /api/print/markdown](#post-apiprintmarkdown-markdown-打印)
  - [POST /api/print/barcode](#post-apiprintbarcode-条码打印)
  - [POST /api/print/word](#post-apiprintword-word-文档打印)
  - [POST /api/print/pdf](#post-apiprintpdf-pdf-打印)
  - [POST /api/print/table](#post-apiprinttable-表格打印)
  - [POST /api/print/schedule](#post-apiprintschedule-课程表打印)
- [常见错误排查](#常见错误排查)

## 启用服务

1. 打开 QrintPrint，进入「我的 → 设置」页面；
2. 找到「远程打印服务」，勾选「启用远程打印服务」；
3. 服务启动后在设置页会显示访问地址，例如 `http://192.168.1.100:8512`；
4. 默认端口 `8512`，可在设置页修改（1024-65535）；
5. 在「API Key 管理」区域创建 Key，复制令牌作为调用凭证。

配置持久化保存在 `%APPDATA%\QrintPrint\api_prefs.json`。

## 鉴权与多 Key 权限

除 `/api/health` 外，所有接口都需要在请求头中携带某个已创建 Key 的令牌：

```
X-Api-Token: <你的 Token>
```

### 两种 Key

| 类型 | 权限 |
|------|------|
| **管理员 Key** | 自动拥有全部接口权限，适合自用 |
| **普通 Key** | 仅能访问「接口权限」中勾选的接口，适合分发给他人使用（例如只开放文本打印） |

在设置页「API Key 管理」中可创建任意数量的 Key，每个 Key 可独立勾选允许访问的接口。删除 Key 后对应令牌立即失效。

### 错误响应

- 令牌不匹配返回 `401`：

```json
{ "ok": false, "message": "无效的 API Token" }
```

- 令牌有效但无权访问该接口返回 `403`：

```json
{ "ok": false, "message": "Key 'xxx' 无权访问该接口: /api/print/xxx" }
```

## 通用约定

- 接口支持 `GET` / `POST`，请求体为 JSON（`Content-Type: application/json`）；
- 请求体大小上限 **10MB**（Word / PDF 需先转 Base64，请控制文件大小）；
- 成功响应：

```json
{ "ok": true, "message": "打印成功" }
```

- 失败响应统一为 `{ "ok": false, "message": "错误描述" }`，常见状态码：

| 状态码 | 含义 |
|--------|------|
| 400 | 请求参数错误 / JSON 解析失败 / 文档解析失败 |
| 401 | Token 无效 |
| 403 | Token 有效但无权访问该接口 |
| 404 | 接口不存在 |
| 413 | 请求体超过 10MB |
| 500 | 服务器内部错误（如打印机未连接、打印失败） |

- 打印任务在服务内部**串行排队**，并发请求不会同时占用打印机；
- 打印成功后会计入桌面端的历史记录，可在历史页重打。

## 接口列表

### GET /api/health 健康检查

免鉴权，用于服务发现。

**请求示例**

```bash
curl http://192.168.1.100:8512/api/health
```

**响应**

```json
{ "ok": true, "app": "QrintPrint", "version": "1.1.0" }
```

### GET /api/status 查询打印机状态

查询当前打印机连接与运行状态。

**请求示例**

```bash
curl -H "X-Api-Token: <你的 Token>" http://192.168.1.100:8512/api/status
```

**响应**

```json
{
  "ok": true,
  "connected": true,
  "mode": "bluetooth",
  "deviceName": "Qring-BY288",
  "batteryPercent": 85,
  "batteryLabel": "85%",
  "paperState": "正常",
  "hardwareState": "正常",
  "thickness": 3,
  "busy": false,
  "bluetoothStatusAvailable": true
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| connected | bool | 打印机是否已连接 |
| mode | string | 连接方式：`bluetooth` / `usb` / `none` |
| deviceName | string/null | 设备名称，未连接时为 null |
| batteryPercent | number/null | 电量百分比，蓝牙不可用时为 null |
| batteryLabel | string | 电量中文标签 |
| paperState | string | 纸张状态（正常 / 缺纸等） |
| hardwareState | string | 硬件状态（正常 / 开盖等） |
| thickness | number | 当前打印浓度（1-5） |
| busy | bool | 是否正在打印 |
| bluetoothStatusAvailable | bool | 蓝牙状态通道是否可用（USB 模式下若未连蓝牙则为 false，状态字段显示 "—"） |

### POST /api/print/text 文本打印

打印普通文本，支持字体样式调节与 LaTeX 公式（用 `$...$` 包裹，需开启 `formulaMode`）。

**请求体**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| content | string | 必填 | 要打印的文本内容 |
| fontSize | number | 24 | 字号（像素） |
| bold | bool | false | 加粗 |
| italic | bool | false | 斜体 |
| underline | bool | false | 下划线 |
| letterSpacing | number | 0 | 字间距（像素） |
| lineSpacing | number | 6 | 行间距（像素） |
| margin | number | 8 | 左右边距（像素） |
| formulaMode | bool | false | 开启后 `$...$` 内的内容按 LaTeX 公式渲染 |
| formulaScale | number | 100 | 公式渲染缩放（50-200，仅 formulaMode 时生效） |

**请求示例**

```bash
curl -X POST http://192.168.1.100:8512/api/print/text \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d '{
    "content": "Hello QrintPrint!\n公式: $E=mc^2$",
    "fontSize": 24,
    "bold": true,
    "formulaMode": true
  }'
```

**响应**

```json
{ "ok": true, "message": "打印成功" }
```

### POST /api/print/image 图片打印

打印图片。图片需先转为 **Base64** 后放入 `imageBase64` 字段。

**请求体**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| imageBase64 | string | 必填 | 图片数据的 Base64 编码（支持 PNG / JPG / BMP 等） |
| ditherMode | string/number | `floyd` | 抖动算法：`none` / `atkinson` / `floyd`（或用数字 0 / 1 / 2） |
| threshold | number | 128 | 二值化阈值（0-255），**仅在 ditherMode 为 `none` 时生效** |
| thickness | number | 默认浓度 | 打印浓度（1-5），不传则使用设置页默认值 |

**请求示例**

```bash
# 先生成图片 Base64
BASE64=$(base64 -w0 photo.png)

curl -X POST http://192.168.1.100:8512/api/print/image \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d "{
    \"imageBase64\": \"$BASE64\",
    \"ditherMode\": \"floyd\",
    \"thickness\": 3
  }"
```

### POST /api/print/markdown Markdown 打印

渲染 Markdown 后打印，支持标题（`#`）、粗体、斜体、行内代码（`` ` ``）、列表（`-`）、引用（`>`）、分割线（`---`）。

**请求体**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| content | string | 必填 | Markdown 文本内容 |
| fontSize | number | 24 | 基础字号（像素），标题自动放大 1.4 倍 |
| margin | number | 8 | 左右边距（像素） |

**请求示例**

```bash
curl -X POST http://192.168.1.100:8512/api/print/markdown \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d '{
    "content": "# 购物清单\n- 牛奶\n- 鸡蛋\n\n> 记得打印后关打印机",
    "fontSize": 20
  }'
```

**响应**

```json
{ "ok": true, "message": "打印成功" }
```

### POST /api/print/barcode 条码打印

生成并打印条码 / 二维码。支持 13 种码制。

**请求体**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| content | string | 必填 | 条码内容（各码制有字符与长度限制，不合规返回 400） |
| codeType | string | `QR_CODE` | 码制，传格式名或显示名均可（见下表） |
| width | number | 384 | 条码宽度（像素，最小 50） |
| height | number | 一维 140 / 二维 384 | 条码高度（像素，最小 30） |
| margin | number | 1 | 条码静区（像素） |
| thickness | number | 默认浓度 | 打印浓度（1-5），不传则使用设置页默认值 |

**支持的码制**（`codeType` 传格式名或显示名，忽略大小写、空格、`-`、`_`）：

| 格式名 | 显示名 | 类别 |
|--------|--------|------|
| EAN_13 | EAN-13 | 一维 |
| EAN_8 | EAN-8 | 一维 |
| UPC_A | UPC-A | 一维 |
| UPC_E | UPC-E | 一维 |
| ITF | ITF-14 | 一维 |
| CODE_128 | Code 128 | 一维 |
| CODE_39 | Code 39 | 一维 |
| CODE_93 | Code 93 | 一维 |
| CODABAR | Codabar | 一维 |
| QR_CODE | QR Code | 二维 |
| DATA_MATRIX | Data Matrix | 二维 |
| PDF_417 | PDF417 | 二维 |
| AZTEC | Aztec | 二维 |

**请求示例**

```bash
# 二维码
curl -X POST http://192.168.1.100:8512/api/print/barcode \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d '{
    "content": "https://example.com",
    "codeType": "QR_CODE",
    "width": 384,
    "height": 384
  }'

# 一维码
curl -X POST http://192.168.1.100:8512/api/print/barcode \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d '{
    "content": "6901234567892",
    "codeType": "EAN-13"
  }'
```

### POST /api/print/word Word 文档打印

打印 `.docx` 文档。支持段落文本、LaTeX 公式（`$...$`）、表格（绘制真实边框网格）与图片（按阈值二值化）。

**请求体**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| fileBase64 | string | 必填 | `.docx` 文件内容的 Base64 编码 |
| fontSize | number | 24 | 字号（像素） |
| bold | bool | false | 整体加粗 |
| italic | bool | false | 整体斜体 |
| lineSpacing | number | 6 | 行间距（像素） |
| margin | number | 8 | 左右边距（像素） |
| imageThreshold | number | 128 | 图片二值化阈值（0-255，越低越黑） |

**请求示例**

```bash
BASE64=$(base64 -w0 document.docx)

curl -X POST http://192.168.1.100:8512/api/print/word \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d "{
    \"fileBase64\": \"$BASE64\",
    \"fontSize\": 20,
    \"imageThreshold\": 150
  }"
```

### POST /api/print/pdf PDF 打印

打印 PDF 文档。两种模式可选：

- **文本模式**（默认）：按字体重排文字、自动分页；表格不渲染为网格，图片按阈值二值化。
- **整页图片模式**：`mode="page"`，将每页 PDF 渲染为图片后逐页打印，保留原始排版。

**请求体**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| fileBase64 | string | 必填 | PDF 文件内容的 Base64 编码 |
| mode | string | text | `text` 文本模式 / `page` 整页图片模式 |
| fontSize | number | 24 | 字号（像素，文本模式） |
| bold | bool | false | 整体加粗（文本模式） |
| italic | bool | false | 整体斜体（文本模式） |
| lineSpacing | number | 6 | 行间距（像素，文本模式） |
| margin | number | 8 | 左右边距（像素） |
| imageThreshold | number | 128 | 图片/整页二值化阈值（0-255，越低越黑） |

**请求示例**

```bash
BASE64=$(base64 -w0 document.pdf)

curl -X POST http://192.168.1.100:8512/api/print/pdf \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d "{
    \"fileBase64\": \"$BASE64\",
    \"mode\": \"page\",
    \"imageThreshold\": 140
  }"
```

### POST /api/print/table 表格打印

打印数据表格（带边框网格，表头加粗，列宽按内容自适应）。

**请求体**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| headers | string[] | 必填 | 表头，1-8 列 |
| rows | string[][] | 必填 | 数据行，1-20 行，每行元素个数可与表头不同（不足补空，多余忽略） |
| fontSize | number | 24 | 字号（像素） |
| margin | number | 8 | 左右边距（像素） |

**请求示例**

```bash
curl -X POST http://192.168.1.100:8512/api/print/table \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d '{
    "headers": ["科目", "成绩"],
    "rows": [
      ["语文", "92"],
      ["数学", "88"],
      ["英语", "95"]
    ],
    "fontSize": 20
  }'
```

### POST /api/print/schedule 课程表打印

打印课程表。自动生成「节次」列（第1节…第N节）与「周一…周日」表头。

**请求体**

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| days | string[][] | 必填 | 二维数组：每个子数组代表一天（最多 7 天），元素为该天的各节课程；每天节数 1-12 |
| fontSize | number | 24 | 字号（像素） |
| margin | number | 8 | 左右边距（像素） |

**请求示例**

```bash
curl -X POST http://192.168.1.100:8512/api/print/schedule \
  -H "X-Api-Token: <你的 Token>" \
  -H "Content-Type: application/json" \
  -d '{
    "days": [
      ["数学", "语文", "英语"],
      ["物理", "化学", "生物"],
      ["数学", "体育", ""]
    ],
    "fontSize": 20
  }'
```

## 常见错误排查

| 现象 | 原因与处理 |
|------|-----------|
| 返回 401 | Token 不匹配，检查请求头 `X-Api-Token` 是否与设置页一致 |
| 返回 403 | Token 有效但该 Key 未勾选此接口权限，在设置页「API Key 管理」中勾选对应接口 |
| 返回 500 "打印机未连接" | 打印前请先在桌面端连接打印机 |
| 返回 500 "无法打印: ..." | 打印前体检拦截，多为缺纸 / 开盖，检查打印机状态 |
| 连接超时 | 确认服务已启用、手机/电脑与打印机所在电脑在同一局域网，且防火墙放行该端口 |
| 无法访问 `http://ip:8512` | 检查防火墙入站规则，或换一个端口重试 |
