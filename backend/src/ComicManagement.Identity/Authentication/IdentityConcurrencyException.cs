namespace ComicManagement.Identity.Authentication;

public sealed class IdentityConcurrencyException() :
    Exception("The identity operation could not be completed after concurrent updates.");
