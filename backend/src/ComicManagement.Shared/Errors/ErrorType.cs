namespace ComicManagement.Shared.Errors;

public enum ErrorType
{
    Unexpected = 0,
    Validation = 1,
    Authentication = 2,
    Authorization = 3,
    NotFound = 4,
    Conflict = 5,
    BusinessRule = 6,
    RateLimited = 7,
    DependencyUnavailable = 8
}
