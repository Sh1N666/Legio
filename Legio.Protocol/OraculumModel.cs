namespace Legio.Protocol;

/// <summary>
/// Selects which zero-allocation ML / inference strategy the Haruspex Oracle uses.
/// </summary>
public enum OraculumModel
{
    /// <summary>Analytical heuristic baseline (unit-normalized target batch duration + CPU pressure scaling).</summary>
    StaticHeuristic = 0,

    /// <summary>Triangular Fuzzy Numbers (TFN) + online-adaptive Takagi-Sugeno Fuzzy Inference System.</summary>
    FuzzySugeno = 1,

    /// <summary>Kosiński's Ordered Fuzzy Numbers (OFN) capturing both uncertainty and cost trend direction.</summary>
    OrderedFuzzyNumbers = 2,

    /// <summary>Discounted UCB Contextual Bandit over discrete strategy arms.</summary>
    ContextualBandit = 3,

    /// <summary>
    /// Classical Online Machine Learning: Discounted Linear Contextual Bandit (LinUCB / Online Ridge Regression
    /// trained via exact Recursive Least Squares using the Sherman-Morrison matrix inversion lemma).
    /// </summary>
    OnlineLinUcb = 4
}
