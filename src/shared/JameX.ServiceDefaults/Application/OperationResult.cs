using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JameX.ServiceDefaults.Application;

/// <summary>
/// What an application service returns instead of an <see cref="IResult"/>.
/// <para>
/// The service layer must not know it is being called over HTTP — the same
/// method should be usable from an event handler, a background job or a test
/// with no <c>HttpContext</c> in sight. So it reports an <i>outcome</i>, and
/// translating that outcome into a status code stays at the edge, in
/// <see cref="HttpResultExtensions"/>.
/// </para>
/// <para>
/// The alternative — throwing <c>NotFoundException</c>, <c>ConflictException</c>
/// and friends — works, but uses exceptions for outcomes that are entirely
/// expected. A missing video is not exceptional; it is Tuesday.
/// </para>
/// </summary>
public sealed class OperationResult<T>
{
    private OperationResult(
        ResultStatus status,
        T? value,
        string? error,
        IReadOnlyDictionary<string, string[]>? validationErrors)
    {
        Status = status;
        Value = value;
        Error = error;
        ValidationErrors = validationErrors;
    }

    public ResultStatus Status { get; }
    public T? Value { get; }
    public string? Error { get; }
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; }

    public bool IsSuccess => Status == ResultStatus.Success;

    public static OperationResult<T> Success(T value) => new(ResultStatus.Success, value, null, null);

    public static OperationResult<T> NotFound(string? error = null) =>
        new(ResultStatus.NotFound, default, error, null);

    public static OperationResult<T> Conflict(string error) =>
        new(ResultStatus.Conflict, default, error, null);

    /// <summary>
    /// The caller is known but not allowed. Distinct from
    /// <see cref="NotFound"/>: 404 says "no such thing", 403 says "it exists
    /// and it is not yours".
    /// </summary>
    public static OperationResult<T> Forbidden(string error) =>
        new(ResultStatus.Forbidden, default, error, null);

    public static OperationResult<T> Invalid(string field, string message) =>
        new(ResultStatus.Invalid, default, null,
            new Dictionary<string, string[]> { [field] = [message] });
}

public enum ResultStatus
{
    Success = 0,
    NotFound = 1,
    Conflict = 2,
    Invalid = 3,
    Forbidden = 4
}

/// <summary>
/// The one place that knows how an outcome maps to a status code. Keeping the
/// mapping in a single method is what stops one controller answering 404 and
/// another 400 for the same situation.
/// <para>
/// This is the <i>only</i> file that had to change when the transport moved
/// from minimal APIs to controllers — the service and repository layers never
/// knew which one they were behind.
/// </para>
/// </summary>
public static class ActionResultExtensions
{
    public static IActionResult ToActionResult<T>(
        this OperationResult<T> result, Func<T, IActionResult>? onSuccess = null) =>
        result.Status switch
        {
            ResultStatus.Success => onSuccess is null
                ? new OkObjectResult(result.Value)
                : onSuccess(result.Value!),
            ResultStatus.NotFound => result.Error is null
                ? new NotFoundResult()
                : new NotFoundObjectResult(new { error = result.Error }),
            ResultStatus.Conflict => new ConflictObjectResult(new { error = result.Error }),
            ResultStatus.Forbidden => new ObjectResult(new { error = result.Error })
            {
                StatusCode = StatusCodes.Status403Forbidden
            },
            ResultStatus.Invalid => new BadRequestObjectResult(
                new ValidationProblemDetails(
                    result.ValidationErrors!.ToDictionary(e => e.Key, e => e.Value))),
            _ => new ObjectResult(new ProblemDetails { Title = "Unhandled result status." })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            }
        };
}
