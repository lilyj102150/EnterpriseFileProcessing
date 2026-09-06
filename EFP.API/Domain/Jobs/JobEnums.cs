namespace EFP.API.Domain.Jobs;

public enum CFileJobType { ImportExcel, ExportExcel, GeneratePdf, ValidateFile }
public enum CFileJobStatus { Pending, Admitted, Running, Completing, Succeeded, Failed, Cancelled, Expired }
public enum CFileJobPhase { Queued, Uploading, Inspecting, ReadingWorkbook, ReadingSheet, ValidatingRows, PersistingRows, WritingWorkbook, WritingSheet, RenderingPage, FinalizingArtifact, VerifyingArtifact, Completed }
public enum CProgressMetricType { Bytes, Rows, Pages, Sheets, Files, Records }
public enum CFileStorageProvider { Local, S3Compatible }
public enum CJobPriority { Low, Normal, High, Critical }