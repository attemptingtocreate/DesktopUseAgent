namespace SemanticDesktop.Core.Errors;

public static class ErrorCodes
{
    public const string StaleTarget = "STALE_TARGET";
    public const string NotFound = "NOT_FOUND";
    public const string AmbiguousTarget = "AMBIGUOUS_TARGET";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string Timeout = "TIMEOUT";
    public const string Unsupported = "UNSUPPORTED";
    public const string Internal = "INTERNAL";
    public const string Cancelled = "CANCELLED";
    public const string ProcessFailed = "PROCESS_FAILED";
    public const string PatternUnavailable = "PATTERN_UNAVAILABLE";
    public const string ConditionTimeout = "CONDITION_TIMEOUT";
    public const string StepFailed = "STEP_FAILED";
    public const string ReferenceError = "REFERENCE_ERROR";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string ApprovalRequired = "APPROVAL_REQUIRED";
    public const string ApprovalDenied = "APPROVAL_DENIED";
    public const string ApprovalTimeout = "APPROVAL_TIMEOUT";
    public const string EmergencyStopped = "EMERGENCY_STOPPED";
    public const string PathNotAllowed = "PATH_NOT_ALLOWED";
    public const string AdapterNotFound = "ADAPTER_NOT_FOUND";
    public const string AdapterUnavailable = "ADAPTER_UNAVAILABLE";
    public const string AdapterFailed = "ADAPTER_FAILED";
    public const string VisionFailed = "VISION_FAILED";
    public const string IncompatibleSchema = "INCOMPATIBLE_SCHEMA";
    public const string IntegrityFailed = "INTEGRITY_FAILED";
    public const string UpdateFailed = "UPDATE_FAILED";
}
