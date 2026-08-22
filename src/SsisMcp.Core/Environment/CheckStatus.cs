namespace SsisMcp.Core.Environment
{
    /// <summary>Outcome of a single environment probe.</summary>
    public enum CheckStatus
    {
        /// <summary>Requirement satisfied.</summary>
        Pass,

        /// <summary>Present but with caveats the caller should read.</summary>
        Warn,

        /// <summary>Requirement not met.</summary>
        Fail,

        /// <summary>Could not be determined (probe error), not necessarily a failure.</summary>
        Unknown
    }
}
