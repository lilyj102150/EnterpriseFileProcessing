# Enterprise File Processing WebAPI — .NET 10

## 0. Contract của plan

Đây là implementation plan, không phải mô tả kiến trúc chung. Mỗi phase phải tạo ra code chạy được, migration chạy được, API test được và có acceptance criteria.

Mục tiêu:

- .NET 10 ASP.NET Core WebAPI.
- Một WebAPI project/executable duy nhất.
- Excel master/template cần giữ formatting/layout/business semantics.
- Excel import/export từ vài trăm nghìn tới hàng triệu row.
- File có thể > 1 GB.
- 1 triệu request không được hiểu là một request đồng bộ giữ server thread; hệ thống phải hấp thụ request, durable-queue job và phân phối workload.
- Không để một job lớn chiếm hết RAM, CPU, disk hoặc DB connection pool.
- Upload/download lớn phải stream/bounded-buffer.
- Import/export phải có progress có ngữ nghĩa: byte, sheet, row, accepted row, rejected row, written row, page, file size, phase, ETA, throughput.
- Progress phải phản ánh công việc thực tế, không phải timer giả.
- Scale-out nhiều instance mà không duplicate job.
- Runtime policy không được khóa cứng vào `appsettings.json`.
- Request/Response của API là `record`.
- Enum bắt đầu bằng `C`.
- Code không comment. Ngoại lệ duy nhất: XML docs cho API và Request/Response record.
- Sau implementation phải có `Tests/REST/Test.http` covering tất cả test case API.

---

# 1. Những thứ tuyệt đối không được làm

## 1.1 Không dùng appsettings làm runtime control plane

Không thiết kế kiểu:

```json
{
  "FileProcessing": {
    "MaxConcurrentImports": 2,
    "MaxConcurrentExports": 2,
    "QueueCapacity": 100
  }
}
```

Các giá trị trên có thể tồn tại dưới dạng bootstrap safety floor, nhưng không phải source of truth cho capacity/runtime policy.

Runtime policy phải nằm ở durable control plane:

- Database tables.
- Distributed lease.
- Tenant policy.
- Worker capability.
- Global safety policy.
- Runtime counters/metrics.

`appsettings` chỉ chứa:

- Connection bootstrap.
- Storage provider bootstrap.
- Distributed coordination endpoint/bootstrap.
- Logging bootstrap.
- OpenTelemetry bootstrap.
- Hard safety upper bound không được phép vượt qua lúc runtime.

Thay đổi runtime phải không cần redeploy.

## 1.2 Không tạo một queue khổng lồ trong RAM

Không dùng:

```csharp
new ConcurrentQueue<Job>();
```

cho hàng triệu job.

Queue durable phải nằm ngoài process:

- PostgreSQL durable queue table cho baseline.
- `SKIP LOCKED` để claim job.
- Lease để crash recovery.
- Index theo status/priority/next_attempt_at.

In-memory `Channel<T>` chỉ dùng cho local dispatch buffer nhỏ sau khi job đã durable trong DB.

## 1.3 Không coi ClosedXML là streaming row engine

ClosedXML xây dựng workbook object model. Tài liệu/source và benchmark của dự án cho thấy memory tăng đáng kể khi workbook lớn; benchmark của repository ghi nhận 1,000,000 x 10 text cells có used memory khoảng 801 MiB ở load và khoảng 1.88 GiB trong save benchmark. ClosedXML cũng nêu rõ không thread-safe. Vì vậy ClosedXML không được dùng làm đường import/export arbitrary-million-row mặc định. 

Source:

- https://github.com/ClosedXML/ClosedXML
- https://github.com/ClosedXML/ClosedXML/releases
- https://raw.githubusercontent.com/ClosedXML/ClosedXML/develop/ClosedXML/Excel/XLWorkbook.cs
- https://raw.githubusercontent.com/ClosedXML/ClosedXML/develop/ClosedXML/Excel/LoadOptions.cs

## 1.4 Không export Excel > memory budget bằng MemoryStream

Cấm:

```csharp
using var stream = new MemoryStream();
workbook.SaveAs(stream);
return stream.ToArray();
```

Các issue của ClosedXML có lịch sử OOM khi SaveAs vào MemoryStream với workbook lớn. 

## 1.5 Không import Excel lớn bằng `new XLWorkbook(stream)`

ClosedXML load workbook model; đó là wrong abstraction cho million-row streaming import.

Million-row import phải sử dụng Open XML SAX/read pipeline với `OpenXmlReader` hoặc `XmlReader` ở tầng row/cell.

## 1.6 Không dùng DataTable/List<T> toàn file cho import

Cấm:

```csharp
var rows = await repository.GetAllAsync();
var table = new DataTable();
```

để phục vụ million-row processing.

Phải dùng:

```text
source stream
  -> row reader
  -> row validator
  -> bounded batch
  -> bulk persistence
  -> progress sink
```

## 1.7 Không tạo PDF byte[] cho report lớn

Không gọi API trả `byte[]` cho large report.

QuestPDF source hiện tại có `GeneratePdf(Stream)` và implementation sử dụng stream abstraction; `GeneratePdf()` không có stream tạo `MemoryStream` rồi trả `ToArray()`. Vì vậy production large-report path phải đi qua file-backed stream và benchmark package version pin cụ thể. 

Source:

- https://github.com/QuestPDF/QuestPDF
- https://github.com/QuestPDF/QuestPDF/blob/main/Source/QuestPDF/Fluent/GenerateExtensions.cs
- https://github.com/QuestPDF/QuestPDF/blob/main/Source/QuestPDF/Drawing/DocumentGenerator.cs

## 1.8 Không stream progress bằng polling-only nếu client cần real-time

API polling vẫn phải tồn tại cho recovery.

Kênh realtime chính:

- SignalR hoặc SSE.

Kênh durable:

- `GET /api/v1/jobs/{jobId}/progress`.

Realtime event không được là source of truth; DB snapshot là source of truth.

---

# 2. Production architecture

```text
                    ┌──────────────────────────┐
                    │        Clients           │
                    └────────────┬─────────────┘
                                 │
                         HTTPS / REST / SSE
                                 │
                    ┌────────────▼─────────────┐
                    │ ASP.NET Core WebAPI       │
                    │ API + Auth + Rate Limit   │
                    │ Upload Init / Job Create  │
                    └──────┬───────────┬────────┘
                           │           │
                 direct upload        │ durable command
                           │           │
                ┌──────────▼───┐   ┌──▼───────────────────┐
                │ Object Store │   │ PostgreSQL            │
                │ S3-compatible│   │ Jobs / Progress /     │
                │ multipart    │   │ Idempotency / Policy  │
                └──────┬───────┘   └─────────┬─────────────┘
                       │                     │
                       │              durable claim/lease
                       │                     │
                ┌──────▼────────────────────▼──────┐
                │ Worker processes / worker pods    │
                │ Excel Import                      │
                │ Excel Export                      │
                │ PDF Report                        │
                │ Cleanup / Recovery                │
                └──────────────┬────────────────────┘
                               │
                     ┌─────────▼──────────┐
                     │ Progress publisher │
                     │ durable snapshot   │
                     │ realtime events    │
                     └────────────────────┘
```

Một executable vẫn chứa API + worker role code. Deployment có thể chạy:

- API-only replicas.
- Worker replicas.
- Worker capability theo environment variable/role.

Không cần tách solution thành nhiều project.

---

# 3. Scale strategy cho 1 triệu request

## 3.1 Phân biệt request và work

Một HTTP request không đồng nghĩa một long-running CPU task.

Request path chỉ:

```text
authenticate
validate
idempotency
create job
persist command
return 202
```

Job path:

```text
claim
lease
stage/read
process
persist progress
artifact finalize
complete
```

## 3.2 Direct-to-object-storage upload

Đối với file > configured threshold, client không POST toàn bộ file qua WebAPI.

API:

```text
POST /api/v1/uploads
```

trả multipart upload session:

```text
uploadId
objectKey
partSize
expiresAt
requiredHeaders
completeUrl
abortUrl
```

Client upload trực tiếp vào object storage.

Sau khi complete:

```text
POST /api/v1/uploads/{uploadId}/complete
```

API verify:

- object exists.
- total bytes.
- part count.
- checksum.
- content type policy.
- declared filename.
- tenant quota.

Sau đó tạo import job.

Điều này tránh việc 1 triệu upload lớn biến WebAPI thành network bottleneck.

## 3.3 Small upload

File dưới direct-upload threshold có thể stream qua API vào staging/object storage.

ASP.NET Core hỗ trợ `HttpRequest.BodyReader` và `HttpResponse.BodyWriter`; pipeline phải ưu tiên PipeReader/PipeWriter hoặc Stream khi API cần abstraction tương thích. 

Source:

- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/request-response?view=aspnetcore-10.0

---

# 4. Runtime Control Plane

## 4.1 Mục tiêu

Không hard-code concurrency.

Không yêu cầu restart để thay đổi:

- tenant limits.
- per-operation limits.
- priority.
- maximum upload size.
- direct-upload threshold.
- retry policy.
- lease timeout.
- progress cadence.
- worker reservation.
- global admission policy.

## 4.2 Tables

### `RuntimePolicies`

```text
Id
PolicyKey
Version
IsActive
PayloadJson
CreatedAt
UpdatedAt
```

### `TenantPolicies`

```text
TenantId
Version
MaxActiveJobs
MaxRunningImports
MaxRunningExports
MaxRunningReports
MaxInputBytesPerDay
MaxOutputBytesPerDay
MaxFileBytes
MaxRowsPerJob
MaxPriority
IsEnabled
UpdatedAt
```

### `WorkerCapabilities`

```text
WorkerId
InstanceId
Role
CpuCount
MemoryBytes
TempDiskBytes
MaxImportSlots
MaxExportSlots
MaxPdfSlots
LastHeartbeatAt
Version
```

### `RuntimeLeases`

```text
LeaseId
WorkerId
JobId
LeaseType
AcquiredAt
ExpiresAt
HeartbeatAt
Version
```

### `ConcurrencyReservations`

```text
ReservationId
TenantId
JobId
ResourceType
Units
CreatedAt
ReleasedAt
```

### `SystemLoadSnapshots`

```text
Id
CapturedAt
CpuPercent
ProcessWorkingSetBytes
GcHeapBytes
DiskFreeBytes
DbActiveConnections
DbPoolUtilization
RunningJobs
QueueDepth
```

## 4.3 Runtime evaluator

Create:

```text
Application/Common/RuntimePolicy/
    IRuntimePolicyProvider.cs
    IRuntimeAdmissionService.cs
    IWorkerCapacityProvider.cs
    IConcurrencyReservationService.cs
    ISystemLoadProvider.cs
```

Evaluation input:

```text
job type
tenant
requested rows
input bytes
estimated output bytes
current queue depth
current tenant active jobs
current worker CPU
current worker memory
current worker temp disk
DB pool utilization
```

Evaluation output:

```csharp
public sealed record RuntimeAdmissionDecision(
    bool Accepted,
    string? Reason,
    int Priority,
    int ReservedUnits,
    TimeSpan LeaseDuration);
```

## 4.4 Dynamic concurrency algorithm

Không chạy:

```csharp
new SemaphoreSlim(options.MaxConcurrentImports);
```

thay vào đó:

```text
capacity = min(
    worker_cpu_capacity,
    worker_memory_capacity,
    worker_disk_capacity,
    tenant_capacity,
    global_capacity
)
```

Ví dụ import slot cost:

```text
base cost = 1
+ large-file penalty
+ large-row penalty
+ template complexity penalty
```

Worker không được phép nhận job nếu reservation làm:

```text
CPU headroom < safety margin
OR
available temp disk < required temp disk
OR
DB connection headroom < reserved DB cost
OR
memory headroom < estimated peak memory
```

Không dùng chính xác công thức này như production default; phase benchmark phải xác định coefficient từ workload thật.

---

# 5. `.csproj` package boundary

Package baseline:

```xml
<ItemGroup>
  <PackageReference Include="ClosedXML" Version="0.105.1" />
  <PackageReference Include="QuestPDF" Version="2026.8.0" />
  <PackageReference Include="DocumentFormat.OpenXml" Version="..." />
  <PackageReference Include="Asp.Versioning.Http" Version="..." />
  <PackageReference Include="Microsoft.EntityFrameworkCore" Version="..." />
  <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="..." PrivateAssets="all" />
  <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="..." />
  <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="..." />
  <PackageReference Include="FluentValidation.AspNetCore" Version="..." />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="..." />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="..." />
  <PackageReference Include="Microsoft.AspNetCore.SignalR.Protocols.MessagePack" Version="..." />
</ItemGroup>
```

Version của các package Microsoft phải pin theo cùng .NET 10 servicing baseline khi implementation.

Không dùng floating version.

Không để `*`.

---

# 6. Project structure

```text
EnterpriseFileWebApi/
├── EnterpriseFileWebApi.csproj
├── Program.cs
├── GlobalUsings.cs
├── Api/
│   ├── Controllers/
│   │   ├── UploadsController.cs
│   │   ├── ImportsController.cs
│   │   ├── ExportsController.cs
│   │   ├── ReportsController.cs
│   │   ├── JobsController.cs
│   │   ├── FilesController.cs
│   │   └── ProgressController.cs
│   ├── Contracts/
│   │   ├── Uploads/
│   │   ├── Imports/
│   │   ├── Exports/
│   │   ├── Reports/
│   │   ├── Jobs/
│   │   └── Files/
│   ├── Filters/
│   └── Extensions/
├── Application/
│   ├── Uploads/
│   ├── Imports/
│   ├── Exports/
│   ├── Reports/
│   ├── Jobs/
│   ├── Progress/
│   ├── Storage/
│   ├── RuntimePolicy/
│   ├── Idempotency/
│   └── Common/
├── Domain/
│   ├── Jobs/
│   ├── Files/
│   ├── Imports/
│   ├── Exports/
│   ├── Reports/
│   └── RuntimePolicy/
├── Infrastructure/
│   ├── Persistence/
│   ├── Storage/
│   ├── Excel/
│   │   ├── ClosedXml/
│   │   └── OpenXml/
│   ├── Pdf/
│   ├── Runtime/
│   ├── Progress/
│   ├── Locking/
│   └── Observability/
└── Tests/
    ├── Unit/
    ├── Integration/
    ├── Performance/
    └── REST/
        └── Test.http
```

---

# 7. Strict code rules

## 7.1 Request/Response

Tất cả Request/Response phải là record.

Đúng:

```csharp
public sealed record CreateImportRequest(
    Guid UploadId,
    string? TemplateCode,
    bool ValidateOnly);
```

Đúng:

```csharp
/// <summary>
/// Represents the import creation result.
/// </summary>
public sealed record CreateImportResponse(
    Guid JobId,
    CFileJobStatus Status,
    string StatusUrl,
    string ProgressUrl);
```

Không dùng class cho API DTO.

## 7.2 Enum

```csharp
public enum CFileJobStatus
{
    Pending,
    Admitted,
    Running,
    Completing,
    Succeeded,
    Failed,
    Cancelled,
    Expired
}
```

Tất cả enum bắt đầu `C`.

## 7.3 Không comment

Cấm:

```csharp
// initialize queue
```

Cấm:

```csharp
/* process batch */
```

Ngoại lệ duy nhất:

- XML docs API.
- XML docs Request/Response record.

## 7.4 Async

Không `.Result`.

Không `.Wait()`.

Không `Task.Run` trong controller.

Không CPU-bound processing trên request path.

## 7.5 Logging

Structured logging only.

Không log:

- client secret.
- access token.
- file content.
- row content.
- signed URL.
- raw PII.

---

# 8. Domain model

## `CFileJobType`

```csharp
public enum CFileJobType
{
    ImportExcel,
    ExportExcel,
    GeneratePdf,
    ValidateFile
}
```

## `CFileJobStatus`

```csharp
public enum CFileJobStatus
{
    Pending,
    Admitted,
    Running,
    Completing,
    Succeeded,
    Failed,
    Cancelled,
    Expired
}
```

## `CFileJobPhase`

```csharp
public enum CFileJobPhase
{
    Queued,
    Uploading,
    Inspecting,
    ReadingWorkbook,
    ReadingSheet,
    ValidatingRows,
    PersistingRows,
    WritingWorkbook,
    WritingSheet,
    RenderingPage,
    FinalizingArtifact,
    VerifyingArtifact,
    Completed
}
```

## `CProgressMetricType`

```csharp
public enum CProgressMetricType
{
    Bytes,
    Rows,
    Pages,
    Sheets,
    Files,
    Records
}
```

## `CFileStorageProvider`

```csharp
public enum CFileStorageProvider
{
    Local,
    S3Compatible
}
```

---

# 9. Database schema

## 9.1 `file_jobs`

Columns:

```text
id uuid pk
tenant_id bigint not null
job_type smallint not null
status smallint not null
phase smallint not null
priority smallint not null
attempt integer not null
max_attempt integer not null
input_bytes bigint null
input_rows bigint null
output_bytes bigint null
output_rows bigint null
output_pages bigint null
sheet_count integer null
current_sheet integer null
current_row bigint null
total_rows bigint null
accepted_rows bigint not null
rejected_rows bigint not null
processed_rows bigint not null
progress_percent numeric(9,6) not null
throughput_value numeric(20,4) null
throughput_unit smallint null
eta_seconds bigint null
created_at timestamptz not null
started_at timestamptz null
completed_at timestamptz null
next_attempt_at timestamptz null
lease_owner varchar(128) null
lease_expires_at timestamptz null
cancel_requested_at timestamptz null
error_code varchar(128) null
error_message varchar(2048) null
input_object_key varchar(1024) null
output_object_key varchar(1024) null
row_count_strategy smallint not null
version bigint not null
```

## 9.2 `file_job_progress`

Append-only event history:

```text
id bigint generated always as identity
job_id uuid not null
sequence bigint not null
phase smallint not null
metric_type smallint not null
current_value bigint not null
total_value bigint null
percent numeric(9,6) null
sheet_index integer null
sheet_name varchar(255) null
row_index bigint null
accepted_rows bigint null
rejected_rows bigint null
processed_rows bigint null
bytes_read bigint null
bytes_written bigint null
pages_rendered bigint null
files_completed bigint null
throughput_value numeric(20,4) null
throughput_unit smallint null
eta_seconds bigint null
captured_at timestamptz not null
```

Unique:

```text
(job_id, sequence)
```

## 9.3 `idempotency_records`

```text
tenant_id bigint
idempotency_key varchar(128)
request_hash char(64)
resource_type varchar(64)
resource_id uuid
created_at timestamptz
expires_at timestamptz
```

Unique:

```text
tenant_id
idempotency_key
```

## 9.4 `job_dependencies`

```text
job_id uuid
depends_on_job_id uuid
sequence integer
```

## 9.5 `storage_objects`

```text
id uuid
tenant_id bigint
object_key varchar(1024)
provider smallint
content_length bigint
sha256 char(64)
content_type varchar(255)
created_at timestamptz
expires_at timestamptz
state smallint
```

## 9.6 Indexes

Bắt buộc:

```text
file_jobs(status, priority desc, created_at)
file_jobs(status, next_attempt_at)
file_jobs(tenant_id, status)
file_jobs(lease_expires_at)
file_job_progress(job_id, sequence desc)
storage_objects(expires_at)
idempotency_records(expires_at)
```

Partial indexes phải dùng cho trạng thái active khi workload benchmark xác nhận lợi ích.

---

# 10. Durable queue implementation

## 10.1 Claim SQL

Worker không đọc toàn bộ queue.

Dùng transaction:

```sql
WITH candidate AS
(
    SELECT id
    FROM file_jobs
    WHERE status IN (0, 1)
      AND (next_attempt_at IS NULL OR next_attempt_at <= now())
      AND (lease_expires_at IS NULL OR lease_expires_at < now())
    ORDER BY priority DESC, created_at
    FOR UPDATE SKIP LOCKED
    LIMIT @batch_size
)
UPDATE file_jobs j
SET status = 2,
    lease_owner = @worker_id,
    lease_expires_at = now() + @lease_duration,
    started_at = COALESCE(started_at, now()),
    version = version + 1
FROM candidate c
WHERE j.id = c.id
RETURNING j.*;
```

`SKIP LOCKED` cho phép nhiều worker claim song song mà không chờ nhau trên các row đã bị lock.

## 10.2 Lease heartbeat

Trong worker:

```text
claim
start heartbeat
process
heartbeat every lease/3
complete or fail
release lease
```

Nếu process chết:

```text
lease expires
recovery worker thấy expired
job trở lại Pending
attempt++
```

## 10.3 Poison job

Nếu attempt >= max_attempt:

```text
Failed
```

Không retry vô hạn.

---

# 11. Admission control

## 11.1 Request admission

Trước khi tạo job:

```text
validate auth
validate tenant
validate idempotency
validate input metadata
check tenant quota
check daily byte quota
check active job quota
calculate estimated resource cost
```

Nếu vượt quota:

```http
429 Too Many Requests
```

Nếu job hợp lệ nhưng capacity chưa đủ:

```http
202 Accepted
```

Job ở Pending, không tạo background task.

## 11.2 Priority

```csharp
public enum CJobPriority
{
    Low,
    Normal,
    High,
    Critical
}
```

Tenant policy giới hạn priority tối đa.

Không cho client tự gửi `Critical` nếu tenant policy không cho phép.

---

# 12. Upload architecture

## 12.1 Upload session request

```csharp
/// <summary>
/// Creates an upload session for a source file.
/// </summary>
public sealed record CreateUploadRequest(
    string FileName,
    long ContentLength,
    string ContentType,
    string Sha256);
```

Response:

```csharp
/// <summary>
/// Represents the upload session.
/// </summary>
public sealed record CreateUploadResponse(
    Guid UploadId,
    string ObjectKey,
    int PartSize,
    int ExpectedPartCount,
    DateTimeOffset ExpiresAt,
    string CompleteUrl,
    string AbortUrl);
```

## 12.2 Large file upload

Client:

```text
CreateUpload
    -> multipart upload
    -> CompleteUpload
    -> CreateImportJob
```

API không proxy toàn bộ 1 GB file qua controller.

## 12.3 Small file upload

Endpoint stream trực tiếp request body vào object storage/local staging.

Không materialize body thành `byte[]`.

## 12.4 Upload verification

Complete phải xác minh:

```text
content-length
part-count
part checksum
whole-object checksum if provider supports
content-type policy
extension policy
magic bytes
```

Extension không đủ để xác định file type.

---

# 13. File staging model

Mỗi job có:

```text
input/
output/
working/
checkpoint/
```

Object naming:

```text
{tenant}/{jobId}/input/{fileId}
{tenant}/{jobId}/output/{artifactId}
{tenant}/{jobId}/working/{name}
```

Không dùng user filename làm storage key.

Storage key phải collision-safe.

---

# 14. Excel import strategy

## 14.1 Engine selection

### Template/master workbook

ClosedXML chỉ dùng khi business cần:

- inspect workbook metadata.
- manipulate formatting.
- read small/controlled sheets.
- preserve template-specific constructs.

### Large row ingestion

Dùng Open XML low-level reader.

Pipeline:

```text
OpenXmlPackage
    -> WorkbookPart
    -> SharedStringTable reader
    -> WorksheetPart
    -> OpenXmlReader
    -> row decoder
    -> cell decoder
    -> row mapper
    -> validation
    -> bounded batch
    -> persistence
```

Không tạo DOM cho toàn worksheet.

## 14.2 Cell reading

Row reader phải output:

```csharp
public readonly record struct ExcelRow(
    long RowNumber,
    ReadOnlyMemory<ExcelCell> Cells);
```

`ExcelCell` phải giữ primitive/string reference vừa đủ cho current row.

Không giữ tất cả row object.

## 14.3 Shared strings

Không load shared-string table toàn bộ nếu workbook quá lớn mà policy không cho phép.

Nếu package structure yêu cầu random lookup:

- memory budget được tính riêng.
- shared string count phải được inspect trước.
- nếu vượt threshold, reject hoặc dùng controlled spill strategy.

Không dùng unbounded dictionary.

## 14.4 Row progress

Mỗi row có logical progress:

```text
processedRows = validatedRows + rejectedRows
```

Nếu total row xác định:

```text
progressPercent = processedRows / totalRows * 100
```

Nếu total row chưa xác định:

```text
progressPercent = phase-level indeterminate
```

Không được dựng `%` giả bằng thời gian.

## 14.5 Row-level event

Internal progress state:

```csharp
public readonly record struct ImportProgressState(
    long ProcessedRows,
    long AcceptedRows,
    long RejectedRows,
    long? TotalRows,
    long BytesRead,
    long CurrentRow,
    int? CurrentSheetIndex,
    string? CurrentSheetName);
```

## 14.6 Bounded batch

Không:

```csharp
var allRows = new List<Row>();
```

Dùng:

```text
batch size = adaptive 256..4096 rows
```

Adaptive dựa trên:

- average row bytes.
- DB latency.
- current memory.
- transaction duration.

Batch size không phải config cứng trong appsettings.

Runtime worker có thể điều chỉnh.

---

# 15. Import persistence

## 15.1 Staging table

Với import business lớn:

```text
raw staging rows
    -> validation
    -> merge/upsert
```

Không insert từng row với một transaction.

## 15.2 Bulk path

Ưu tiên PostgreSQL binary COPY/Npgsql bulk path cho staging table.

Pipeline:

```text
row reader
 -> mapper
 -> batch validation
 -> binary COPY
 -> flush
 -> checkpoint
```

## 15.3 Transaction boundary

Không transaction toàn file.

Transaction theo batch hoặc checkpoint window.

Job completion chỉ set Succeeded sau khi toàn bộ batch đã commit.

## 15.4 Resume

Checkpoint:

```text
jobId
sheetIndex
rowNumber
sourceOffset if reliable
lastCommittedBatch
```

Nếu parser không thể resume bằng byte offset do ZIP/XML boundaries, resume theo logical row checkpoint + replay bounded window.

---

# 16. Import error model

Hai loại lỗi:

### Fatal

- corrupt ZIP.
- invalid workbook package.
- unsupported encryption.
- storage unavailable.
- schema mismatch at structural level.

Job fail.

### Row error

- invalid date.
- missing required field.
- invalid enum.
- duplicate business key.

Row reject nhưng job có thể tiếp tục.

Persist reject record:

```text
job_id
sheet_index
sheet_name
row_number
column_name
error_code
error_message
```

Không lưu full row content nếu không có policy bảo vệ dữ liệu.

---

# 17. Enterprise progress model

## 17.1 Progress phải có state machine

Không dùng:

```text
percentage = elapsed / expectedTime
```

Phải dùng denominator thật khi có thể.

## 17.2 Import

Progress event:

```json
{
  "jobId": "...",
  "phase": "ReadingSheet",
  "fileIndex": 1,
  "fileTotal": 3,
  "sheetIndex": 2,
  "sheetTotal": 8,
  "currentSheet": "Orders",
  "currentRow": 182340,
  "processedRows": 180000,
  "totalRows": 250000,
  "acceptedRows": 179500,
  "rejectedRows": 500,
  "percent": 72.000000,
  "bytesRead": 734003200,
  "inputBytes": 1073741824,
  "throughputRowsPerSecond": 4210.4,
  "throughputBytesPerSecond": 1720320,
  "etaSeconds": 166
}
```

## 17.3 Export

Export progress:

```text
rows queried
rows rendered
rows written
bytes written
sheets completed
current row
current sheet
output bytes
```

Nếu total rows có từ query count/exact source:

```text
percent = renderedRows / totalRows * 100
```

Nếu query không có count:

- phase progress theo source cursor không được giả mạo 100%.
- report là `indeterminate` cho phase row retrieval.

## 17.4 PDF

PDF progress:

```text
current page
estimated total pages if determinable
rendered pages
completed sections
bytes written
```

Nếu total pages không biết trước:

```text
page current + phase state
```

Không tự dựng total pages giả.

## 17.5 File-level aggregation

Nhiều file:

```text
overall = weighted(file progress)
```

Weight mặc định theo total rows/bytes/page khi denominator thật tồn tại.

Ví dụ:

```text
file weight = totalRows / sum(totalRows)
```

Không dùng average percentage đơn giản nếu file sizes chênh lệch lớn.

---

# 18. Progress throttling

## 18.1 Progress publisher

Mỗi worker có local state.

Không write DB mỗi row.

Flush durable progress khi:

```text
rows delta >= threshold
OR
bytes delta >= threshold
OR
time delta >= threshold
OR
phase changed
OR
sheet changed
OR
job completed/failed/cancelled
```

Threshold được runtime policy controlled.

Ví dụ safety default:

```text
min time = 250 ms
min rows = 100
min bytes = 1 MiB
```

Không phải appsettings source of truth.

## 18.2 Event stream

DB snapshot cập nhật theo batch.

Realtime event phát sau successful durable snapshot.

Guarantee:

```text
durable state first
realtime notification second
```

Nếu SignalR disconnect, client GET progress snapshot.

---

# 19. Progress API

## `GET /api/v1/jobs/{jobId}/progress`

Response:

```csharp
/// <summary>
/// Represents the current durable job progress.
/// </summary>
public sealed record JobProgressResponse(
    Guid JobId,
    CFileJobStatus Status,
    CFileJobPhase Phase,
    decimal? Percent,
    long? CurrentRow,
    long? TotalRows,
    long ProcessedRows,
    long AcceptedRows,
    long RejectedRows,
    long BytesRead,
    long? InputBytes,
    long BytesWritten,
    long? OutputBytes,
    long PagesRendered,
    long? TotalPages,
    int? SheetIndex,
    int? SheetTotal,
    string? SheetName,
    decimal? RowsPerSecond,
    decimal? BytesPerSecond,
    long? EtaSeconds,
    DateTimeOffset CapturedAt);
```

## `GET /api/v1/jobs/{jobId}/progress/events`

SSE hoặc SignalR.

Event phải chứa sequence number.

Client reconnect phải gửi last seen sequence.

---

# 20. Cancellation

API:

```http
POST /api/v1/jobs/{jobId}/cancel
```

Request:

```csharp
/// <summary>
/// Requests cooperative cancellation for a job.
/// </summary>
public sealed record CancelJobRequest(
    string Reason);
```

Cancel semantics:

```text
Pending -> Cancelled
Admitted -> Cancelled
Running -> cancellation_requested -> cleanup -> Cancelled
Completed -> 409
```

Worker kiểm tra cancellation:

- trước mỗi batch.
- sau mỗi storage operation.
- trước mỗi DB commit.
- trước mỗi expensive workbook operation.
- trước mỗi PDF page/section boundary khi API hỗ trợ.

---

# 21. Excel export strategy

## 21.1 Decision tree

```text
rows <= small threshold
AND workbook complexity is high
    -> ClosedXML

rows > threshold
OR estimated memory > worker allowance
    -> Open XML writer
```

Ngưỡng là runtime policy.

## 21.2 OpenXmlWriter path

Pipeline:

```text
data reader
 -> async enumerator
 -> row mapper
 -> OpenXmlWriter
 -> FileStream
 -> object storage upload
```

Không tạo:

- DataTable toàn bộ.
- List toàn bộ result.
- MemoryStream toàn file.

## 21.3 Data reader

Source DB phải trả:

```text
IAsyncEnumerable<T>
```

hoặc data reader forward-only.

Không offset pagination cho million rows nếu có keyset alternative.

Dùng keyset pagination hoặc server-side cursor tùy query pattern.

## 21.4 Excel row limit

Excel worksheet row limit phải được enforce.

Khi vượt limit:

```text
create next sheet
```

Progress:

```text
sheetIndex
sheetTotal if known
currentRow
rowsWritten
```

## 21.5 Style strategy

Không tạo style object cho từng cell.

Dùng:

```text
shared style
column style
header style
number format
```

Template rendering chỉ ở phase template preparation.

---

# 22. ClosedXML master/template export

ClosedXML dùng cho master workbook khi format/layout là yêu cầu.

Flow:

```text
open template from object storage
 -> validate template version
 -> create worker-local temp file
 -> XLWorkbook(templatePath)
 -> modify controlled ranges
 -> SaveAs(tempFile)
 -> verify zip package
 -> upload artifact
```

Không giữ workbook lâu hơn cần thiết.

Không chạy nhiều ClosedXML workbooks đồng thời trong cùng worker nếu memory budget không đủ.

ClosedXML không thread-safe; mỗi workbook phải có isolated execution context. 

---

# 23. PDF report strategy

## 23.1 Large report

Flow:

```text
job
 -> load report definition
 -> fetch page/section data incrementally
 -> QuestPDF document
 -> file-backed stream
 -> artifact finalize
```

Không:

```csharp
var bytes = document.GeneratePdf();
```

## 23.2 Resource reservation

PDF job reservation phải tính:

```text
estimated pages
asset count
image bytes
font resources
working disk
memory allowance
```

## 23.3 Report chunking

Nếu business cho phép:

```text
report 1..N sections
 -> render section artifact
 -> combine final PDF
```

Chunking chỉ dùng khi document semantics vẫn đúng.

## 23.4 Version pinning

QuestPDF major/minor phải pin.

Mọi package upgrade bắt buộc chạy:

```text
100k pages benchmark
500k pages benchmark
large asset benchmark
memory benchmark
cancellation benchmark
artifact integrity benchmark
```

---

# 24. Download large artifact

API:

```http
GET /api/v1/files/{fileId}/content
```

Flow:

```text
authorize
 -> HEAD metadata
 -> stream from object storage
 -> response BodyWriter/Stream
```

Nếu object storage hỗ trợ signed URL và policy cho phép, API nên trả signed URL thay vì proxy bytes qua application.

Response:

```csharp
/// <summary>
/// Represents a downloadable file reference.
/// </summary>
public sealed record FileDownloadResponse(
    Guid FileId,
    string FileName,
    string ContentType,
    long ContentLength,
    string DownloadUrl,
    DateTimeOffset ExpiresAt);
```

Large data path ưu tiên direct storage download.

---

# 25. HTTP API surface

## Uploads

```text
POST   /api/v1/uploads
POST   /api/v1/uploads/{uploadId}/complete
DELETE /api/v1/uploads/{uploadId}
GET    /api/v1/uploads/{uploadId}
```

## Imports

```text
POST /api/v1/imports
GET  /api/v1/imports/{jobId}
GET  /api/v1/imports/{jobId}/errors
```

## Exports

```text
POST /api/v1/exports
GET  /api/v1/exports/{jobId}
```

## Reports

```text
POST /api/v1/reports
GET  /api/v1/reports/{jobId}
```

## Jobs

```text
GET  /api/v1/jobs/{jobId}
POST /api/v1/jobs/{jobId}/cancel
GET  /api/v1/jobs/{jobId}/progress
GET  /api/v1/jobs/{jobId}/progress/events
```

## Files

```text
GET    /api/v1/files/{fileId}
GET    /api/v1/files/{fileId}/content
DELETE /api/v1/files/{fileId}
```

---

# 26. Idempotency

Tất cả command endpoint phải nhận:

```http
Idempotency-Key: <128-char-safe-value>
```

Flow:

```text
BEGIN
 -> insert idempotency record
 -> create job
 -> COMMIT
```

Nếu duplicate:

```text
same hash -> return original response
same key + different hash -> 409
```

Không tạo duplicate import job khi client retry do network timeout.

---

# 27. Database connection governance

Không cho worker mỗi job tự tạo nhiều DbContext concurrency.

Mỗi worker có bounded DB concurrency reservation.

Không dùng:

```text
Parallel.ForEachAsync(... 1000)
```

cho database operations.

Query path:

```text
keyset/cursor
 -> bounded async reader
 -> transform
 -> batch
```

---

# 28. Memory governance

## Worker memory budget

Mỗi job có:

```text
EstimatedMemoryCost
ReservedMemory
ObservedPeakMemory
```

Worker admission:

```text
sum(reserved memory) + process safety margin <= worker memory budget
```

Worker phải đo:

- `GC.GetTotalMemory`.
- process working set.
- runtime GC counters.

Không dùng `GC.Collect()` như flow control.

GC forced chỉ được phép ở controlled benchmark/diagnostic, không phải normal path.

---

# 29. Disk governance

Large Excel/PDF cần temp disk.

Mỗi job phải reserve:

```text
required temp bytes
```

Trước processing:

```text
free disk - reserved temp bytes >= safety margin
```

Job nếu không đủ:

```text
remain Pending
reason = InsufficientDiskCapacity
```

Không start rồi mới hết disk.

Cleanup job chạy độc lập:

```text
expired input
expired output
orphan working files
aborted multipart uploads
```

---

# 30. Backpressure

Backpressure phải tồn tại ở mọi boundary:

```text
HTTP
  -> storage
  -> parser
  -> row channel
  -> validator
  -> DB
  -> artifact writer
  -> storage
```

Use bounded `Channel<T>` hoặc `System.IO.Pipelines`.

Ví dụ:

```csharp
var channel = Channel.CreateBounded<ExcelRow>(new BoundedChannelOptions(1024)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleWriter = true,
    SingleReader = true
});
```

Capacity là worker runtime policy, không phải appsettings source of truth.

---

# 31. Producer/consumer import

```text
Reader
  -> Channel<ExcelRow>
Validator
  -> Channel<ValidatedRow>
Batcher
  -> DB writer
```

Producer phải `await channel.Writer.WriteAsync`.

Nếu consumer chậm, producer dừng.

Không drop row.

Không uncontrolled parallelism.

---

# 32. Export producer/consumer

```text
DB reader
  -> bounded row channel
Excel writer
  -> FileStream
  -> artifact store
```

Nếu Excel writer chậm, DB reader giảm tốc độ.

Không đọc toàn result set vào RAM.

---

# 33. Progress correctness rules

## Rule 1

`percent` chỉ có giá trị numeric nếu denominator có semantics thật.

## Rule 2

Không lấy `fileSize` làm totalRows.

## Rule 3

Không lấy elapsed time làm percentage.

## Rule 4

Nếu totalRows unknown:

```json
"percent": null
```

và client hiển thị phase progress/throughput.

## Rule 5

`processedRows = acceptedRows + rejectedRows` nếu mỗi input row có đúng một terminal classification.

## Rule 6

Một row không được tính processed trước khi persistence/validation stage tương ứng hoàn thành.

## Rule 7

Export row count chỉ increment sau khi row đã được materialize vào output writer.

## Rule 8

`bytesWritten` lấy từ actual destination write count, không dự đoán.

## Rule 9

PDF `pagesRendered` increment sau khi page hoàn tất.

## Rule 10

Overall progress phải aggregate từ completed work units, không trung bình phần trăm một cách mù quáng.

---

# 34. Throughput và ETA

## 34.1 Sliding window

Dùng rolling window 5–30 giây tùy workload.

```text
throughput = delta_work / delta_time
```

ETA:

```text
remaining = total - current
eta = remaining / smoothedThroughput
```

Khi throughput chưa ổn định:

```text
eta = null
```

Không show ETA rác như `0s` trong vài giây đầu.

## 34.2 Metrics

Import:

```text
rows/sec
bytes/sec
rows rejected/sec
DB batch latency
parse latency
```

Export:

```text
rows/sec
bytes/sec
DB read latency
writer latency
```

PDF:

```text
pages/sec
bytes/sec
layout latency
render latency
asset load latency
```

---

# 35. Observability

## Logs

Mỗi job phải có:

```text
TraceId
JobId
TenantId
JobType
Attempt
WorkerId
Phase
```

## Metrics

```text
file_jobs_created_total
file_jobs_started_total
file_jobs_completed_total
file_jobs_failed_total
file_jobs_cancelled_total
file_jobs_queue_depth
file_jobs_active
file_job_duration_seconds
file_job_rows_total
file_job_bytes_total
file_job_progress_updates_total
file_job_retry_total
file_job_lease_expired_total
file_job_admission_rejected_total
worker_cpu_utilization
worker_memory_utilization
worker_temp_disk_utilization
db_pool_utilization
```

## Tracing

Span:

```text
upload
job.create
job.claim
excel.inspect
excel.read.sheet
excel.validate.batch
excel.persist.batch
excel.write.sheet
pdf.render.page
artifact.finalize
```

---

# 36. Security

## File security

Validate:

```text
extension
magic bytes
package structure
content-type
max size
max sheet count
max row count
max cell count
max shared string count
```

## Zip bomb protection

Before processing ZIP-based formats:

```text
compressed size
uncompressed estimate if available
entry count
compression ratio
nested archive policy
```

Reject dangerous ratios/structures.

## Path traversal

Never extract user-controlled filenames directly.

---

# 37. API error contract

Response:

```csharp
/// <summary>
/// Represents an API error.
/// </summary>
public sealed record ApiErrorResponse(
    string Code,
    string Message,
    string TraceId,
    IReadOnlyList<ApiFieldError> Errors);
```

Không trả stack trace.

Không trả exception message nguyên bản cho security-sensitive error.

---

# 38. Cancellation and shutdown

Graceful shutdown:

```text
stop admitting new jobs
stop claiming new jobs
finish checkpoint-safe active work
renew lease until shutdown deadline
persist cancellation/requeue state
exit
```

Không hard-kill đang ghi artifact nếu có thể tránh.

Job lease phải timeout-safe.

---

# 39. Worker loop pseudocode

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    var jobs = await jobRepository.ClaimAsync(workerContext, stoppingToken);

    if (jobs.Count == 0)
    {
        await delayPolicy.DelayAsync(stoppingToken);
        continue;
    }

    foreach (var job in jobs)
    {
        if (!await admissionService.TryReserveAsync(job, workerContext, stoppingToken))
        {
            await jobRepository.ReleaseLeaseAsync(job.Id, stoppingToken);
            continue;
        }

        try
        {
            await processor.ProcessAsync(job, stoppingToken);
            await jobRepository.MarkSucceededAsync(job.Id, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await jobRepository.RequeueAsync(job.Id, stoppingToken);
            throw;
        }
        catch (OperationCanceledException)
        {
            await jobRepository.MarkCancelledAsync(job.Id, stoppingToken);
        }
        catch (Exception exception)
        {
            await failureHandler.HandleAsync(job, exception, stoppingToken);
        }
        finally
        {
            await admissionService.ReleaseAsync(job.Id, stoppingToken);
        }
    }
}
```

Actual implementation phải tách processor, admission, lease, progress và failure handler; không giữ tất cả trong một class.

---

# 40. Excel import interfaces

```csharp
public interface IExcelImportProcessor
{
    ValueTask ProcessAsync(
        FileJob job,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IExcelRowReader
{
    IAsyncEnumerable<ExcelRow> ReadAsync(
        Stream source,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IExcelRowBatchWriter
{
    ValueTask<BatchWriteResult> WriteAsync(
        IReadOnlyList<ValidatedExcelRow> rows,
        CancellationToken cancellationToken);
}
```

---

# 41. Excel export interfaces

```csharp
public interface IExcelExportProcessor
{
    ValueTask<FileArtifact> ProcessAsync(
        ExportJob job,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IRowSource<T>
{
    IAsyncEnumerable<T> ReadAsync(
        ExportQuery query,
        CancellationToken cancellationToken);
}
```

```csharp
public interface ISpreadsheetWriter<T>
{
    ValueTask WriteAsync(
        IAsyncEnumerable<T> rows,
        Stream destination,
        SpreadsheetWriteContext context,
        CancellationToken cancellationToken);
}
```

---

# 42. Artifact finalization

Artifact state machine:

```text
Creating
Writing
Verifying
Available
Expired
Deleted
Corrupted
```

Finalize:

```text
flush
fsync where required
close writer
calculate/check checksum
validate ZIP/PDF signature
persist metadata transactionally
publish artifact available
```

Client chỉ download khi state = `Available`.

---

# 43. Integrity verification

Excel:

- ZIP package opens successfully.
- required workbook parts exist.
- worksheets valid.

PDF:

- file opens by validation parser where feasible.
- EOF marker present.
- file length non-zero.

Object storage:

- content length exact.
- checksum matches.

---

# 44. Runtime policy API

Không expose toàn bộ internal worker config cho public clients.

Admin-only endpoints:

```text
GET   /api/v1/admin/runtime-policies
PUT   /api/v1/admin/runtime-policies/{key}
GET   /api/v1/admin/worker-capacity
GET   /api/v1/admin/load
GET   /api/v1/admin/queue
```

Request:

```csharp
/// <summary>
/// Updates a runtime policy version.
/// </summary>
public sealed record UpdateRuntimePolicyRequest(
    JsonElement Payload,
    long ExpectedVersion);
```

Optimistic concurrency bắt buộc.

---

# 45. Dynamic control examples

Admin có thể thay đổi:

```text
ImportLargeFileMemoryCost
ExportMemoryCost
PdfMemoryCost
MaxTenantActiveJobs
ProgressRowsThreshold
ProgressBytesThreshold
ProgressTimeThresholdMs
LeaseDurationSeconds
RetryLimit
MaxSheetCount
MaxRowsPerJob
MaxFileBytes
```

Thay đổi phải:

```text
persist version
invalidate local cache
broadcast policy changed
workers reload
```

Không restart service.

---

# 46. Cache runtime policy

Local cache TTL ngắn.

Distributed invalidation:

```text
policy update
 -> DB commit
 -> notification
 -> instances reload
```

Không coi cache là source of truth.

Nếu notification mất:

```text
TTL refresh
```

---

# 47. Tenant isolation

Mọi job query bắt buộc include tenant scope.

Không cho API nhận arbitrary `tenantId` từ body rồi tin tưởng.

Tenant lấy từ authenticated principal/context.

Repository phải có tenant predicate ở layer bắt buộc.

---

# 48. Multi-instance correctness

Không dùng local mutex cho correctness.

Không dùng:

```csharp
static readonly SemaphoreSlim GlobalLock = ...;
```

cho distributed guarantee.

Distributed correctness dựa trên:

- PostgreSQL row locks.
- lease columns.
- unique constraints.
- idempotency.
- optimistic concurrency.

---

# 49. Local development mode

Development có thể dùng local filesystem storage.

Production phải có object-storage contract.

Storage interface:

```csharp
public interface IObjectStorage
{
    ValueTask<StorageObjectMetadata> GetMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken);

    ValueTask<StorageWriteSession> OpenWriteAsync(
        string objectKey,
        StorageWriteOptions options,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken);
}
```

---

# 50. Configuration boundary

`appsettings.json` chỉ được có các nhóm bootstrap:

```json
{
  "ConnectionStrings": {
    "Database": ""
  },
  "Storage": {
    "Provider": "S3Compatible",
    "Endpoint": "",
    "Bucket": "",
    "Region": ""
  },
  "OpenTelemetry": {
    "Endpoint": ""
  },
  "BootstrapSafety": {
    "AbsoluteMaxFileBytes": 2147483648,
    "AbsoluteMaxConcurrentJobsPerProcess": 32
  }
}
```

`BootstrapSafety` là upper hard guard, không phải operational target.

Ví dụ database policy có thể nói `MaxConcurrentImports = 8`; process không vượt `AbsoluteMaxConcurrentJobsPerProcess = 32`.

Không đặt hàng chục tham số nghiệp vụ/runtime vào appsettings.

---

# 51. API versioning

URL:

```text
/api/v1/...
```

Mặc định:

```text
v1
```

Versioning phải support future v2 contract mà không breaking v1.

---

# 52. Controller rule

Controller chỉ làm:

```text
bind
authorize
validate
call application
map response
```

Controller không:

- parse Excel.
- query million rows.
- render PDF.
- open workbook.
- create background Task.

---

# 53. Application command example

```csharp
public sealed record CreateImportCommand(
    Guid UploadId,
    string? TemplateCode,
    bool ValidateOnly,
    long TenantId,
    string IdempotencyKey);
```

Handler:

```csharp
public interface ICreateImportHandler
{
    ValueTask<CreateImportResult> HandleAsync(
        CreateImportCommand command,
        CancellationToken cancellationToken);
}
```

Handler flow:

```text
idempotency lookup
 -> upload validation
 -> tenant quota
 -> resource estimation
 -> job insert
 -> outbox event insert
 -> commit
 -> response
```

---

# 54. Outbox pattern

Nếu tạo job và gửi notification phải atomic:

```text
file_jobs
outbox_messages
```

Trong cùng transaction:

```text
insert job
insert outbox
commit
```

Background dispatcher:

```text
read unpublished
publish
mark published
```

Progress notification không được làm job creation rollback.

---

# 55. Worker event flow

```text
Job claimed
 -> phase Running
 -> create progress snapshot
 -> process
 -> progress flush
 -> artifact verify
 -> job Completing
 -> artifact Available
 -> job Succeeded
 -> publish completion event
```

Ordering phải được kiểm soát.

---

# 56. Failure matrix

| Failure | Expected behavior |
|---|---|
| API restart | Job remains durable |
| Worker crash | Lease expires, job requeued |
| DB connection loss | Retry transient operation |
| Object storage timeout | Retry bounded operation |
| Client disconnect upload | Multipart can resume/abort |
| Client disconnect progress | Job continues |
| Client disconnect download | Artifact remains available |
| Import row error | Row rejected, job continues |
| Corrupt workbook | Job failed |
| Disk low | New job admission blocked |
| Memory pressure | New expensive job admission blocked |
| Duplicate POST | Same job returned |
| Duplicate completion | Idempotent |
| Unknown total rows | Percent null |

---

# 57. Retry matrix

Retry only transient:

```text
DB connection reset
network timeout
object store 5xx
lease heartbeat transient failure
```

Do not retry automatically:

```text
invalid workbook
validation error
quota violation
unsupported format
corrupt ZIP
business rule violation
```

Backoff:

```text
exponential + jitter
```

Max retry count controlled by runtime policy.

---

# 58. Health endpoints

```text
GET /health/live
GET /health/ready
GET /health/storage
GET /health/database
GET /health/worker
```

`ready` false khi:

- DB unavailable.
- storage unavailable.
- worker role không đủ dependency.
- migration state incompatible.

---

# 59. Metrics-driven autoscaling

Nếu deploy Kubernetes/container platform, autoscaling không dựa chỉ vào CPU.

Scale signals:

```text
queue depth
oldest pending job age
active jobs
CPU
memory
DB pool utilization
object-storage throughput
```

Ví dụ worker replicas scale theo:

```text
desiredWorkers = ceil(queueWorkCost / workerCapacity)
```

`queueWorkCost` phải dựa job weights, không chỉ job count.

---

# 60. Job cost estimation

Estimator input:

```text
file size
sheet count
row count if inspectable
column count
shared string count
template complexity
requested output format
```

Output:

```csharp
public sealed record JobResourceEstimate(
    long EstimatedMemoryBytes,
    long EstimatedTempDiskBytes,
    long EstimatedOutputBytes,
    long EstimatedRows,
    long EstimatedPages,
    int CpuUnits);
```

Estimator có thể sai; admission phải dùng safety margin và observed feedback.

---

# 61. Adaptive worker capacity

Worker định kỳ phát telemetry:

```text
cpu
memory
GC heap
working set
disk
IO latency
DB latency
```

Runtime policy engine điều chỉnh effective capacity.

Ví dụ:

```text
normal load -> capacity 8
memory pressure -> capacity 4
DB pressure -> capacity 2
low disk -> capacity 0 for disk-heavy jobs
```

Đây là dynamic admission, không restart process.

---

# 62. Bounded internal scheduler

Local worker chỉ giữ số job nhỏ đã claim tương ứng với current capacity.

Không claim 10,000 jobs rồi xếp trong RAM.

Claim count:

```text
min(
    available global capacity,
    available tenant capacity,
    local safe buffer
)
```

---

# 63. File operation isolation

Một large ClosedXML job không được chạy cùng worker slot memory-sensitive khác nếu reservation không đủ.

Resource classes:

```csharp
public enum CResourceClass
{
    Cpu,
    Memory,
    TempDisk,
    Database,
    StorageBandwidth
}
```

Job reservation là vector resources, không phải một integer semaphore đơn giản.

---

# 64. Progress state persistence code shape

```csharp
public interface IProgressReporter
{
    ValueTask ReportAsync(
        JobProgressState state,
        CancellationToken cancellationToken);
}
```

Reporter implementation phải:

```text
compare last persisted state
 -> threshold decision
 -> write snapshot + event
 -> emit realtime notification
```

Không notify mỗi row.

---

# 65. Progress sequence semantics

Mỗi job có monotonic `sequence`.

Client:

```text
sequence 101
sequence 102
sequence 103
```

Nếu client nhận 101 rồi 103:

```text
GET snapshot
```

Realtime event loss không làm mất truth.

---

# 66. Export progress exactness

Nếu query có exact `COUNT(*)`:

```text
totalRows = COUNT
```

Nếu COUNT quá đắt:

```text
use approved estimate
```

Nhưng response phải đánh dấu:

```text
isTotalEstimated = true
```

Khi total estimated, progress percent không được gọi là exact percent.

Model:

```csharp
public sealed record ProgressTotal(
    long? Value,
    bool IsEstimated);
```

---

# 67. Import total-row strategy

Trước import:

```text
inspect workbook metadata
```

Nếu có thể xác định worksheet dimensions mà không đọc toàn DOM:

```text
totalRows = dimension estimate
```

Phải distinguish:

```text
KnownExact
KnownEstimated
Unknown
```

Không hard-code assumption rằng worksheet dimension luôn exact.

---

# 68. Multi-file import

API nhận manifest:

```csharp
public sealed record CreateMultiFileImportRequest(
    IReadOnlyList<Guid> UploadIds,
    string? TemplateCode,
    bool ValidateOnly);
```

Master job:

```text
file job 1
file job 2
...
file job N
```

Aggregate progress:

```text
completedFiles
currentFile
fileTotal
weighted overall progress
```

File child job failure policy phải explicit:

```text
FailFast
ContinueAndReport
```

---

# 69. Report export

Report definition phải lưu:

```text
query definition id
version
parameters hash
template version
locale
timezone
```

Không tin request raw khi job worker xử lý lại.

Persist immutable job input snapshot.

---

# 70. Locale/timezone

Persist timezone ID theo job.

Report rendering phải deterministic.

Không dùng machine-local timezone mặc định.

---

# 71. File retention

Mỗi artifact có TTL.

Retention policy theo:

```text
tenant
job type
artifact type
legal/business requirement
```

Cleanup không chạy trong request.

---

# 72. Background cleanup

Cleanup worker claim records theo batch nhỏ.

Không `DELETE FROM storage_objects` toàn bảng.

Database cleanup dùng keyset/batch delete.

Object delete phải idempotent.

---

# 73. Database migrations

Migration phải:

```text
forward only
backward-compatible during rolling deployment
```

Không drop column ngay nếu production đang có old pod.

Pattern:

```text
expand
migrate
switch
contract
```

---

# 74. API security limit

Configure at endpoint/runtime policy level:

```text
max request body
max JSON depth
max header size where required
rate limit
concurrent request limit
```

Không dùng request-body memory buffering cho large uploads.

---

# 75. Excel security limit

Runtime policy:

```text
MaxFileBytes
MaxWorkbookSheets
MaxRows
MaxColumns
MaxCells
MaxSharedStrings
MaxMergedRanges
MaxDrawingCount
MaxFormulaCount
```

Các limits phải được kiểm tra trước và trong processing.

---

# 76. Template versioning

Master Excel template:

```text
templateCode
templateVersion
sha256
storageKey
schemaVersion
isActive
```

Job lưu immutable `templateVersion`.

Không đọc `latest` giữa chừng khi job đang chạy.

---

# 77. Schema mapping

Import mapping phải explicit:

```text
column name/index
source type
target property
required
nullable
transform
validation
```

Không auto-map mù bằng reflection trong million-row loop.

Compiled mapper được tạo một lần/job.

---

# 78. Row validation performance

Validation hot path phải:

- tránh reflection per cell.
- avoid allocation per error when no error.
- precompile expressions.
- reuse validator context where safe.

Error list phải bounded.

Persist first N row errors inline nếu API cần preview; toàn bộ error log nên lưu riêng/durable artifact nếu quá lớn.

---

# 79. Error report artifact

Nếu 1 triệu row có 300k lỗi, không giữ 300k lỗi trong RAM.

Error artifact:

```text
job_errors.csv
job_errors.xlsx
```

được stream tạo theo batch.

Progress error report riêng:

```text
errorRows
errorArtifactRows
bytesWritten
```

---

# 80. Test strategy

## Unit

Test:

- progress calculation.
- weighted aggregation.
- retry policy.
- admission decisions.
- resource estimation.
- idempotency.
- state transitions.

## Integration

Test:

- PostgreSQL lock/claim.
- lease recovery.
- object storage.
- OpenXML parser.
- ClosedXML template path.
- QuestPDF artifact path.

## Performance

Required fixtures:

```text
10k rows
100k rows
500k rows
1m rows
5m rows
100MB
500MB
1GB
1.5GB
2GB if supported policy
```

---

# 81. Performance acceptance criteria

Không ghi một con số SLA giả trước benchmark.

Phase benchmark phải tạo baseline:

```text
p50
p95
p99
CPU
RSS
GC heap
LOH
allocations
DB latency
storage throughput
rows/sec
bytes/sec
```

Acceptance rule:

```text
No OOM
No unbounded queue growth
No duplicate jobs
No data loss
No corrupted artifacts
No progress regression
No sustained DB pool starvation
No sustained disk exhaustion
```

---

# 82. Load test scenarios

## Scenario A — 1 million lightweight commands

```text
1,000,000 create-job requests
small payload
no large file
```

Measure:

```text
API p50/p95/p99
DB insert throughput
idempotency throughput
queue depth
```

## Scenario B — many medium imports

```text
100,000 jobs
10k–100k rows/job
```

## Scenario C — large import flood

```text
100+ simultaneous large imports
```

Admission must delay jobs instead of saturating memory.

## Scenario D — large export flood

Workers must cap expensive jobs.

## Scenario E — mixed workload

```text
small upload
large import
large export
PDF
status polling
SSE
```

## Scenario F — worker crash

Kill worker at:

```text
10%
50%
90%
```

Expected:

```text
lease recovery
no duplicate committed rows
artifact cleanup
resume/retry semantics
```

---

# 83. Import correctness tests

Required cases:

```text
single sheet
multiple sheets
empty rows
hidden rows
blank cells
shared strings
inline strings
numbers
dates
formulas
errors
unicode
very long strings
invalid dates
invalid enums
duplicate key
missing required column
extra column
reordered columns
corrupt zip
truncated upload
```

---

# 84. Export correctness tests

```text
0 rows
1 row
1k rows
100k rows
1m rows
multiple sheets
sheet rollover
headers
number formats
dates
UTF-8
formulas if supported
frozen panes if template path
styles
```

---

# 85. PDF tests

```text
1 page
100 pages
1k pages
large images
unicode fonts
multiple sections
cancellation
storage write failure
```

Artifact must be readable after generation.

---

# 86. REST `Test.http`

Tạo đúng một file:

```text
Tests/REST/Test.http
```

File phải cover:

```text
health
create upload
get upload
complete upload
abort upload
create import
get import
get progress
subscribe progress
cancel job
create export
get export
create report
get report
get artifact metadata
download artifact
idempotency replay
idempotency conflict
validation error
quota rejection
unauthorized
forbidden
not found
```

---

# 87. REST `Test.http` required shape

```http
@baseUrl = http://localhost:5000
@tenantToken = replace-me
@jobId = replace-me
@uploadId = replace-me
@fileId = replace-me

### Health
GET {{baseUrl}}/health/live
Accept: application/json

### Create upload session
POST {{baseUrl}}/api/v1/uploads
Authorization: Bearer {{tenantToken}}
Idempotency-Key: upload-{{$random.uuid}}
Content-Type: application/json

{
  "fileName": "master.xlsx",
  "contentLength": 1048576,
  "contentType": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  "sha256": "replace-me"
}

### Get job progress
GET {{baseUrl}}/api/v1/jobs/{{jobId}}/progress
Authorization: Bearer {{tenantToken}}
Accept: application/json

### Cancel job
POST {{baseUrl}}/api/v1/jobs/{{jobId}}/cancel
Authorization: Bearer {{tenantToken}}
Content-Type: application/json

{
  "reason": "user_requested"
}
```

Implementation phase phải bổ sung tất cả endpoints và negative cases, không được để `Test.http` là demo vài request.

---

# 88. API XML docs requirement

Controller action phải có XML docs.

Request/Response record có thể có XML docs.

Domain/application/internal code không cần XML docs trừ khi public API contract yêu cầu.

---

# 89. Phase implementation

## Phase 01 — Bootstrap

Create:

```text
.csproj
Program.cs
folders
GlobalUsings.cs
health endpoints
API versioning
OpenAPI
structured logging
```

Acceptance:

```text
build clean
run clean
health green
```

## Phase 02 — PostgreSQL persistence

Create entities, DbContext, mappings, migrations, indexes.

Acceptance:

```text
migration up
CRUD job
concurrency token
```

## Phase 03 — Idempotency

Implement middleware/service + table.

Acceptance:

```text
same key same payload -> same job
same key different payload -> 409
```

## Phase 04 — Storage abstraction

Implement local provider first, S3-compatible provider second.

Acceptance:

```text
stream write
stream read
metadata
delete
checksum
```

## Phase 05 — Upload session

Implement multipart/direct-upload contract.

Acceptance:

```text
1GB object flow
resume parts
complete
abort
```

## Phase 06 — Durable queue

Implement claim/lease/recovery.

Acceptance:

```text
2 workers cannot claim same job
worker crash recovers
```

## Phase 07 — Runtime control plane

Implement policies/worker telemetry/resource reservation.

Acceptance:

```text
change policy without restart
worker reacts
```

## Phase 08 — Progress engine

Implement durable snapshot + event history + realtime.

Acceptance:

```text
sequence monotonic
client reconnect works
percent null when total unknown
```

## Phase 09 — Large Excel import

Implement Open XML row reader + bounded pipeline + DB staging/bulk.

Acceptance:

```text
1m-row fixture
bounded RSS
row correctness
resume semantics
```

## Phase 10 — ClosedXML template import/export

Implement controlled template path only.

Acceptance:

```text
format preservation
no unsafe MemoryStream
memory benchmark
```

## Phase 11 — Large Excel export

Implement OpenXmlWriter path.

Acceptance:

```text
1m rows
stream/file-backed artifact
sheet rollover
progress exactness
```

## Phase 12 — QuestPDF

Implement file-backed report rendering and progress.

Acceptance:

```text
large report
artifact integrity
memory benchmark
```

## Phase 13 — Large download

Implement direct storage/signed URL path.

Acceptance:

```text
1GB download
range if provider/API mode supports
no byte[] materialization
```

## Phase 14 — Failure/recovery

Implement retry, poison jobs, cleanup, orphan recovery.

Acceptance:

```text
worker kill tests
storage failures
DB transient failures
```

## Phase 15 — Load/performance

Run full matrix.

Acceptance:

```text
no OOM
no uncontrolled queue
no data loss
no corruption
```

## Phase 16 — Security hardening

File validation, quotas, auth, authorization, audit.

## Phase 17 — REST Test.http completion

All API scenarios present.

## Phase 18 — Production readiness

Dashboards, alerts, runbook, deployment manifests, migration strategy.

---

# 90. File naming rules

```text
CreateUploadRequest.cs
CreateUploadResponse.cs
CreateImportRequest.cs
CreateImportResponse.cs
JobProgressResponse.cs
CancelJobRequest.cs
```

Không:

```text
Dtos.cs
Models.cs
CommonDto.cs
```

Large catch-all DTO files bị cấm.

---

# 91. Namespace rules

```text
EnterpriseFileWebApi.Api.Contracts.Imports
EnterpriseFileWebApi.Application.Imports
EnterpriseFileWebApi.Domain.Imports
EnterpriseFileWebApi.Infrastructure.Excel.OpenXml
EnterpriseFileWebApi.Infrastructure.Excel.ClosedXml
```

Không dùng namespace `Helpers` chung cho mọi thứ.

---

# 92. Repository rule

Repository không chứa business policy.

Repository chịu trách nhiệm:

```text
SQL
mapping
query shape
persistence
```

Application chịu trách nhiệm:

```text
workflow
state transition
quota
resource admission
```

Domain chịu trách nhiệm:

```text
invariants
state transition rules
```

---

# 93. Storage rule

Không để domain biết:

```text
S3
MinIO
filesystem
```

Domain chỉ biết artifact/object abstraction.

---

# 94. No hidden background task rule

Cấm:

```csharp
_ = Task.Run(...);
```

trong API.

Cấm fire-and-forget không durable.

Mọi background work phải:

```text
DB job
lease
worker
recovery
```

---

# 95. Thread pool protection

Không block thread.

Không sync I/O.

Không process parallelism không có upper bound.

CPU-heavy operation phải đi qua worker admission.

---

# 96. Object storage bandwidth governance

1 triệu download/export có thể làm nghẽn network.

Capacity model phải có:

```text
storage bandwidth units
```

Một job large-output có cost khác job small-output.

---

# 97. API polling protection

Progress polling cũng có quota.

Clients không được polling mỗi 10 ms.

API response có:

```http
ETag
Last-Modified
```

Client có thể `If-None-Match`.

Realtime channel dùng cho active UI.

---

# 98. Progress snapshot endpoint caching

```http
Cache-Control: private, max-age=0, must-revalidate
ETag: "job-version-sequence"
```

Không cache chéo tenant.

---

# 99. Import report response

```csharp
/// <summary>
/// Represents the completed import result.
/// </summary>
public sealed record ImportResultResponse(
    Guid JobId,
    CFileJobStatus Status,
    long TotalRows,
    long AcceptedRows,
    long RejectedRows,
    Guid? ErrorArtifactId,
    DateTimeOffset CompletedAt);
```

---

# 100. Export result response

```csharp
/// <summary>
/// Represents the completed export result.
/// </summary>
public sealed record ExportResultResponse(
    Guid JobId,
    CFileJobStatus Status,
    Guid? ArtifactId,
    long RowsWritten,
    long BytesWritten,
    int SheetsWritten,
    DateTimeOffset CompletedAt);
```

---

# 101. Report result response

```csharp
/// <summary>
/// Represents the completed report result.
/// </summary>
public sealed record ReportResultResponse(
    Guid JobId,
    CFileJobStatus Status,
    Guid? ArtifactId,
    long PagesRendered,
    long BytesWritten,
    DateTimeOffset CompletedAt);
```

---

# 102. Exact progress percentage implementation

```csharp
public static decimal? CalculatePercent(long current, long? total)
{
    if (total is null || total <= 0)
    {
        return null;
    }

    if (current <= 0)
    {
        return 0m;
    }

    if (current >= total.Value)
    {
        return 100m;
    }

    return Math.Round(
        current * 100m / total.Value,
        6,
        MidpointRounding.ToEven);
}
```

Không dùng float.

Không return `99.9999999` do floating point noise.

---

# 103. Weighted overall progress

```csharp
public readonly record struct ProgressWeight(
    long Current,
    long Total,
    bool IsEstimated);
```

Aggregation phải:

```text
sum current / sum total
```

không phải:

```text
average individual percentages
```

khi weights khác nhau.

Nếu tất cả totals unknown:

```text
overall percent = null
```

---

# 104. Row-by-row visualization contract

UI/client có thể hiển thị:

```text
File 2/5
Sheet 4/12 — Orders
Row 182,340 / 250,000
72.936%
Accepted 179,500
Rejected 2,840
Throughput 4,210 rows/s
ETA 16s
Read 734 MB / 1,024 MB
```

Backend phải cung cấp đủ dữ liệu; UI không tự suy đoán total.

---

# 105. File-by-file visualization contract

```text
Files completed 2 / 5
Current file master-03.xlsx
Current sheet Orders
Current row 182340
Overall percent 61.430000
```

Overall percentage dựa weighted work.

---

# 106. Export-by-sheet visualization

```text
Sheet 3 / 8
Rows written 450000
Sheet rows 450000 / 600000
Bytes written 720 MB
Overall rows 1,350,000 / 3,000,000
```

Không lấy sheet number / total sheets làm progress chính nếu sheet sizes khác nhau.

---

# 107. PDF page visualization

Nếu total page known:

```text
Page 740 / 1200
61.666667%
```

Nếu unknown:

```text
Page 740
Percent unavailable
```

Không hiển thị `61.6%` nếu 1200 chỉ là estimate mà response không đánh dấu estimated.

---

# 108. Resource pressure behavior

## Memory pressure

```text
stop admitting memory-heavy jobs
continue light jobs if safe
```

## Disk pressure

```text
stop disk-heavy jobs
allow metadata APIs
```

## DB pressure

```text
reduce import batch concurrency
reduce job claims
```

## Storage bandwidth pressure

```text
reduce large exports/download proxying
prefer signed/direct URLs
```

---

# 109. Graceful degradation

Khi system quá tải:

```text
GET status -> allowed
POST job -> accepted and queued if quota allows
large expensive job -> delayed
realtime progress -> best effort
polling snapshot -> authoritative
```

Không crash process chỉ để “nhận thêm request”.

---

# 110. Operational dashboards

Dashboard 1 — Queue:

```text
queue depth by job type
oldest job age
admitted/running/pending
```

Dashboard 2 — Workers:

```text
CPU
memory
GC
disk
active slots
```

Dashboard 3 — Data:

```text
rows/sec
bytes/sec
errors
retries
```

Dashboard 4 — Reliability:

```text
OOM
timeouts
lease expiry
artifact corruption
```

---

# 111. Alerts

Trigger khi:

```text
oldest pending age > threshold
queue depth sustained high
DB pool > 80–90%
free disk < safety threshold
memory pressure sustained
lease expirations spike
job failure rate spike
artifact verification failures
```

Thresholds thuộc runtime ops policy, không hard-code business logic.

---

# 112. Benchmark package-specific behavior

Mỗi package version pin phải có benchmark riêng.

## ClosedXML

Measure:

```text
load memory
save memory
load latency
save latency
template complexity impact
formula impact
style impact
```

## Open XML

Measure:

```text
reader throughput
writer throughput
allocation
shared string impact
```

## QuestPDF

Measure:

```text
pages/sec
memory peak
stream output behavior
large asset behavior
```

Không lấy benchmark của version cũ để khẳng định version mới.

---

# 113. Package source verification gate

Trước khi implementation mỗi package adapter:

1. Pin exact package version.
2. Đọc source của API public được dùng.
3. Đọc source dependency quan trọng nếu ảnh hưởng memory/streaming.
4. Viết adapter dựa trên behavior quan sát được.
5. Viết benchmark.
6. Viết integration test.
7. Không tự suy đoán semantics.

Đặc biệt:

- ClosedXML `XLWorkbook` load/save path.
- ClosedXML `LoadOptions`.
- Open XML reader/writer path.
- QuestPDF `GeneratePdf(Stream)`.
- QuestPDF document generation path.

---

# 114. Production definition of done

Feature chỉ Done khi:

```text
code
migration
unit tests
integration tests
performance fixture
REST Test.http
observability
failure handling
cancellation
idempotency
progress correctness
artifact verification
```

Thiếu một thành phần thì chưa Done.

---

# 115. Final implementation sequence

```text
01 Bootstrap
02 Persistence
03 Idempotency
04 Storage
05 Upload
06 Durable Queue
07 Lease/Recovery
08 Runtime Control Plane
09 Resource Reservation
10 Progress Engine
11 Realtime Progress
12 Large Excel Import
13 Import Persistence
14 Import Resume
15 ClosedXML Template Path
16 OpenXML Large Export
17 PDF/QuestPDF
18 Artifact Verification
19 Large Download
20 Cleanup
21 Observability
22 Security
23 Load Test
24 Failure Test
25 REST Test.http
26 Production Hardening
```

Không triển khai Excel/PDF trước khi queue + admission + resource governance + artifact storage đã tồn tại.

---

# 116. Core architectural invariant

Hệ thống phải luôn giữ invariant:

```text
HTTP request rate
    !=
CPU task rate
    !=
DB transaction rate
    !=
large-file concurrency
```

Mỗi tầng có capacity riêng và backpressure riêng.

Đây là điều kiện cốt lõi để 1 triệu request không biến thành 1 triệu workbook/PDF đồng thời trong process.

---

# 117. Final non-negotiable rules

```text
No unbounded in-memory queue
No full-file byte[]
No full-file MemoryStream for large artifacts
No DataTable for million-row imports/exports
No whole-result List for large exports
No request-thread long-running import/export
No fire-and-forget jobs
No runtime concurrency controlled only by appsettings
No fake percentage
No fake total rows
No fake ETA
No global process-local lock for distributed correctness
No retry without idempotency analysis
No package behavior assumption without source verification
No comments except XML docs allowed by project rule
All API Request/Response are records
All enum names start with C
One WebAPI project
One REST Test.http containing all API test cases
```

---

# 118. Source references

ASP.NET Core request/response pipelines:

https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/request-response?view=aspnetcore-10.0

ASP.NET Core file upload guidance:

https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0

ClosedXML repository and benchmark information:

https://github.com/ClosedXML/ClosedXML

ClosedXML workbook source:

https://raw.githubusercontent.com/ClosedXML/ClosedXML/develop/ClosedXML/Excel/XLWorkbook.cs

ClosedXML load options source:

https://raw.githubusercontent.com/ClosedXML/ClosedXML/develop/ClosedXML/Excel/LoadOptions.cs

QuestPDF repository:

https://github.com/QuestPDF/QuestPDF

QuestPDF generation extension source:

https://github.com/QuestPDF/QuestPDF/blob/main/Source/QuestPDF/Fluent/GenerateExtensions.cs

QuestPDF document generation source:

https://github.com/QuestPDF/QuestPDF/blob/main/Source/QuestPDF/Drawing/DocumentGenerator.cs

NuGet ClosedXML:

https://www.nuget.org/packages/closedxml/

NuGet QuestPDF:

https://www.nuget.org/packages/QuestPDF/
