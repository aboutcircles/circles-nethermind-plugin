namespace Circles.Pathfinder.Validation;

/// <summary>
/// A single rule violation detected by HubContractValidator.
/// </summary>
/// <param name="Rule">Short rule identifier, e.g. "NoZeroFlow", "IsPermittedFlow".</param>
/// <param name="Message">Human-readable description of the violation.</param>
/// <param name="EdgeIndex">Index of the offending edge in the transfer path (null if rule is global).</param>
/// <param name="Severity">"error" = contract would revert; "warning" = likely bug but may not revert.</param>
public record ValidationViolation(string Rule, string Message, int? EdgeIndex, string Severity);

/// <summary>
/// Result of validating a transfer path against Hub.sol rules.
/// </summary>
/// <param name="IsValid">True if no error-severity violations were found.</param>
/// <param name="Violations">All violations (both error and warning).</param>
public record ValidationResult(bool IsValid, IReadOnlyList<ValidationViolation> Violations);
