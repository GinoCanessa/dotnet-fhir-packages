// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;

namespace FhirPkg.Models;

internal enum FhirSemVerRangeKind
{
    Exact,
    Wildcard,
    Range,
}

internal sealed class FhirSemVerRange
{
    private readonly IReadOnlyList<RangeAlternative> _alternatives;

    private FhirSemVerRange(
        FhirSemVerRangeKind kind,
        IReadOnlyList<RangeAlternative> alternatives)
    {
        Kind = kind;
        _alternatives = alternatives;
    }

    internal FhirSemVerRangeKind Kind { get; }

    internal static FhirSemVerRange Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        string trimmed = expression.Trim();
        string[] alternativeExpressions = trimmed.Split('|', StringSplitOptions.None);
        List<RangeAlternative> alternatives = [];

        foreach (string alternativeExpression in alternativeExpressions)
        {
            string alternative = alternativeExpression.Trim();
            if (alternative.Length == 0)
                throw InvalidExpression(expression);

            alternatives.Add(ParseAlternative(alternative, expression));
        }

        FhirSemVerRangeKind kind = alternatives.Count == 1
            ? alternatives[0].Kind
            : FhirSemVerRangeKind.Range;

        return new FhirSemVerRange(kind, alternatives);
    }

    internal static bool TryParse(
        string? expression,
        [NotNullWhen(true)] out FhirSemVerRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        try
        {
            range = Parse(expression);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal bool IsSatisfiedBy(FhirSemVer version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (version.IsWildcard)
            return false;

        foreach (RangeAlternative alternative in _alternatives)
        {
            if (alternative.IsSatisfiedBy(version))
                return true;
        }

        return false;
    }

    internal bool HasSatisfyingVersionAtOrBelow(
        FhirSemVer ceiling,
        bool allowPreRelease)
    {
        ArgumentNullException.ThrowIfNull(ceiling);

        foreach (RangeAlternative alternative in _alternatives)
        {
            if (alternative.HasSatisfyingVersionAtOrBelow(
                    ceiling,
                    allowPreRelease))
            {
                return true;
            }
        }

        return false;
    }

    private static RangeAlternative ParseAlternative(string alternative, string fullExpression)
    {
        IReadOnlyList<string> rawTerms = SplitOnWhitespace(alternative);

        if (rawTerms.Count == 3 && rawTerms[1] == "-")
        {
            FhirSemVer lower = ParseExactVersion(rawTerms[0], fullExpression);
            FhirSemVer upper = ParseExactVersion(rawTerms[2], fullExpression);

            return new RangeAlternative(
                FhirSemVerRangeKind.Range,
                [
                    new Constraint(ConstraintOperator.GreaterThanOrEqual, lower),
                    new Constraint(ConstraintOperator.LessThanOrEqual, upper),
                ]);
        }

        IReadOnlyList<string> terms = CombineSeparatedOperators(rawTerms, fullExpression);
        if (terms.Count == 1)
            return ParseSingleTerm(terms[0], fullExpression);

        List<Constraint> constraints = [];
        foreach (string term in terms)
        {
            if (!TryParseComparator(term, fullExpression, out Constraint comparator))
                throw InvalidExpression(fullExpression);

            constraints.Add(comparator);
        }

        return new RangeAlternative(FhirSemVerRangeKind.Range, constraints);
    }

    private static RangeAlternative ParseSingleTerm(string term, string fullExpression)
    {
        if (term.StartsWith('^'))
        {
            FhirSemVer lower = ParseExactVersion(term[1..], fullExpression);
            FhirSemVer upper = CreateCaretCeiling(lower, fullExpression);

            return new RangeAlternative(
                FhirSemVerRangeKind.Range,
                [
                    new Constraint(ConstraintOperator.GreaterThanOrEqual, lower),
                    new Constraint(ConstraintOperator.LessThan, upper),
                ]);
        }

        if (term.StartsWith('~'))
        {
            FhirSemVer lower = ParseExactVersion(term[1..], fullExpression);
            FhirSemVer upper = CreateTildeCeiling(lower, fullExpression);

            return new RangeAlternative(
                FhirSemVerRangeKind.Range,
                [
                    new Constraint(ConstraintOperator.GreaterThanOrEqual, lower),
                    new Constraint(ConstraintOperator.LessThan, upper),
                ]);
        }

        if (TryParseComparator(term, fullExpression, out Constraint comparator))
        {
            return new RangeAlternative(
                FhirSemVerRangeKind.Range,
                [comparator]);
        }

        FhirSemVer version = ParseVersion(term, fullExpression);
        ConstraintOperator constraintOperator = version.IsWildcard
            ? ConstraintOperator.Wildcard
            : ConstraintOperator.Equal;
        FhirSemVerRangeKind kind = version.IsWildcard
            ? FhirSemVerRangeKind.Wildcard
            : FhirSemVerRangeKind.Exact;

        return new RangeAlternative(kind, [new Constraint(constraintOperator, version)]);
    }

    private static bool TryParseComparator(
        string term,
        string fullExpression,
        out Constraint comparator)
    {
        ConstraintOperator constraintOperator;
        int operandStart;

        if (term.StartsWith(">=", StringComparison.Ordinal))
        {
            constraintOperator = ConstraintOperator.GreaterThanOrEqual;
            operandStart = 2;
        }
        else if (term.StartsWith("<=", StringComparison.Ordinal))
        {
            constraintOperator = ConstraintOperator.LessThanOrEqual;
            operandStart = 2;
        }
        else if (term.StartsWith('>'))
        {
            constraintOperator = ConstraintOperator.GreaterThan;
            operandStart = 1;
        }
        else if (term.StartsWith('<'))
        {
            constraintOperator = ConstraintOperator.LessThan;
            operandStart = 1;
        }
        else if (term.StartsWith('='))
        {
            constraintOperator = ConstraintOperator.Equal;
            operandStart = 1;
        }
        else
        {
            comparator = default;
            return false;
        }

        FhirSemVer operand = ParseExactVersion(term[operandStart..], fullExpression);
        comparator = new Constraint(constraintOperator, operand);
        return true;
    }

    private static FhirSemVer ParseVersion(string value, string fullExpression)
    {
        if (!FhirSemVer.TryParse(value, out FhirSemVer? version))
            throw InvalidExpression(fullExpression);

        return version;
    }

    private static FhirSemVer ParseExactVersion(string value, string fullExpression)
    {
        FhirSemVer version = ParseVersion(value, fullExpression);
        if (version.IsWildcard)
            throw InvalidExpression(fullExpression);

        return version;
    }

    private static FhirSemVer CreateCaretCeiling(
        FhirSemVer lower,
        string fullExpression)
    {
        try
        {
            if (lower.Major > 0)
                return FhirSemVer.CreateExact(checked(lower.Major + 1), 0, 0);

            if (lower.Minor > 0)
                return FhirSemVer.CreateExact(0, checked(lower.Minor + 1), 0);

            return FhirSemVer.CreateExact(0, 0, checked(lower.Patch + 1));
        }
        catch (OverflowException exception)
        {
            throw new FormatException(
                $"Semantic version range has no representable upper bound: '{fullExpression}'.",
                exception);
        }
    }

    private static FhirSemVer CreateTildeCeiling(
        FhirSemVer lower,
        string fullExpression)
    {
        try
        {
            return FhirSemVer.CreateExact(
                lower.Major,
                checked(lower.Minor + 1),
                0);
        }
        catch (OverflowException exception)
        {
            throw new FormatException(
                $"Semantic version range has no representable upper bound: '{fullExpression}'.",
                exception);
        }
    }

    private static IReadOnlyList<string> SplitOnWhitespace(string value)
    {
        List<string> terms = [];
        int termStart = -1;

        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                if (termStart >= 0)
                {
                    terms.Add(value[termStart..index]);
                    termStart = -1;
                }
            }
            else if (termStart < 0)
            {
                termStart = index;
            }
        }

        if (termStart >= 0)
            terms.Add(value[termStart..]);

        return terms;
    }

    private static IReadOnlyList<string> CombineSeparatedOperators(
        IReadOnlyList<string> rawTerms,
        string fullExpression)
    {
        List<string> terms = [];

        for (int index = 0; index < rawTerms.Count; index++)
        {
            string term = rawTerms[index];
            if (!IsStandaloneOperator(term))
            {
                terms.Add(term);
                continue;
            }

            if (index + 1 >= rawTerms.Count ||
                IsStandaloneOperator(rawTerms[index + 1]) ||
                rawTerms[index + 1] == "-")
            {
                throw InvalidExpression(fullExpression);
            }

            terms.Add(term + rawTerms[index + 1]);
            index++;
        }

        return terms;
    }

    private static bool IsStandaloneOperator(string value) =>
        value is "^" or "~" or "<" or "<=" or ">" or ">=" or "=";

    private static FormatException InvalidExpression(string expression) =>
        new($"Invalid semantic version range: '{expression}'.");

    private static void AddCandidate(
        List<FhirSemVer> candidates,
        FhirSemVer candidate)
    {
        if (!candidates.Contains(candidate))
            candidates.Add(candidate);
    }

    private static void AddNumericCandidates(
        List<FhirSemVer> candidates,
        int major,
        int minor,
        int patch,
        bool allowPreRelease)
    {
        AddCandidate(
            candidates,
            FhirSemVer.CreateExact(
                major,
                minor,
                patch));
        if (!allowPreRelease)
            return;

        if (FhirSemVer.TryParse(
                $"{major}.{minor}.{patch}--",
                out FhirSemVer? minimumPrerelease))
        {
            AddCandidate(
                candidates,
                minimumPrerelease);
        }
    }

    private static void AddSuccessorCandidates(
        List<FhirSemVer> candidates,
        FhirSemVer operand,
        bool allowPreRelease)
    {
        if (operand.IsPreRelease)
        {
            string prerelease = operand.PreRelease!;
            int suffixStart = prerelease.Length;
            while (suffixStart > 0
                   && char.IsAsciiDigit(
                       prerelease[suffixStart - 1]))
            {
                suffixStart--;
            }

            string numericSuffix =
                prerelease[suffixStart..];
            string successorPrerelease =
                numericSuffix.Length == 0
                    ? $"{prerelease}-"
                    : $"{prerelease}-{numericSuffix}";
            if (FhirSemVer.TryParse(
                    $"{operand.Major}.{operand.Minor}.{operand.Patch}-{successorPrerelease}",
                    out FhirSemVer? successor))
            {
                AddCandidate(
                    candidates,
                    successor);
            }

            AddNumericCandidates(
                candidates,
                operand.Major,
                operand.Minor,
                operand.Patch,
                allowPreRelease);
            return;
        }

        if (operand.Patch < int.MaxValue)
        {
            AddNumericCandidates(
                candidates,
                operand.Major,
                operand.Minor,
                operand.Patch + 1,
                allowPreRelease);
            return;
        }

        if (operand.Minor < int.MaxValue)
        {
            AddNumericCandidates(
                candidates,
                operand.Major,
                operand.Minor + 1,
                0,
                allowPreRelease);
            return;
        }

        if (operand.Major < int.MaxValue)
        {
            AddNumericCandidates(
                candidates,
                operand.Major + 1,
                0,
                0,
                allowPreRelease);
        }
    }

    private enum ConstraintOperator
    {
        Equal,
        Wildcard,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
    }

    private readonly record struct Constraint(
        ConstraintOperator Operator,
        FhirSemVer Operand)
    {
        internal bool IsSatisfiedBy(FhirSemVer version) => Operator switch
        {
            ConstraintOperator.Equal => version.Equals(Operand),
            ConstraintOperator.Wildcard => version.Satisfies(Operand),
            ConstraintOperator.LessThan => version < Operand,
            ConstraintOperator.LessThanOrEqual => version <= Operand,
            ConstraintOperator.GreaterThan => version > Operand,
            ConstraintOperator.GreaterThanOrEqual => version >= Operand,
            _ => false,
        };
    }

    private sealed class RangeAlternative
    {
        private readonly IReadOnlyList<Constraint> _constraints;

        internal RangeAlternative(
            FhirSemVerRangeKind kind,
            IReadOnlyList<Constraint> constraints)
        {
            Kind = kind;
            _constraints = constraints;
        }

        internal FhirSemVerRangeKind Kind { get; }

        internal bool IsSatisfiedBy(FhirSemVer version)
        {
            foreach (Constraint constraint in _constraints)
            {
                if (!constraint.IsSatisfiedBy(version))
                    return false;
            }

            return true;
        }

        internal bool HasSatisfyingVersionAtOrBelow(
            FhirSemVer ceiling,
            bool allowPreRelease)
        {
            List<FhirSemVer> candidates = [];
            AddCandidate(
                candidates,
                ceiling);
            AddNumericCandidates(
                candidates,
                0,
                0,
                0,
                allowPreRelease);

            foreach (Constraint constraint in _constraints)
            {
                FhirSemVer operand = constraint.Operand;
                if (!operand.IsWildcard
                    && (allowPreRelease
                        || !operand.IsPreRelease))
                {
                    AddCandidate(
                        candidates,
                        operand);
                }

                AddNumericCandidates(
                    candidates,
                    operand.Major,
                    operand.Minor,
                    operand.Patch,
                    allowPreRelease);
                if (constraint.Operator
                    == ConstraintOperator.GreaterThan)
                {
                    AddSuccessorCandidates(
                        candidates,
                        operand,
                        allowPreRelease);
                }
            }

            return candidates.Any(
                candidate =>
                    candidate <= ceiling
                    && (allowPreRelease
                        || !candidate.IsPreRelease)
                    && IsSatisfiedBy(candidate));
        }
    }
}
