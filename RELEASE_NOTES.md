## What's New — v1.1.0

### Closed-form formula: fully proven for all deficiency values

All theorems for the 2-SAT MIN-UNSAT closed-form formula are now **fully proven** — the document has been upgraded from conjecture to proof. The proof now covers:

- **Prime** d (Burnside over ℤ_d)
- **Power-of-2** d (Burnside over (ℤ₂)^m)
- **Composite non-power-of-2** d (full Burnside over ℤ_d with explicit rotation fixed-point counting)

The previous version left composite d (first occurring at d = 6) as an open question. This is now resolved.

### Bugfix: incorrect results for composite d values

During the proof work, we discovered that the previous A-coefficient formula `C(d,j)/d` was incorrect for composite non-power-of-2 d values (e.g., d = 6, 9, 10, ...). The formula used a simplified Burnside expression that only held for prime d, producing wrong counts when d had non-trivial divisors.

**Fix:** The formula code now computes A(d,j) via full Burnside's lemma over the cyclic group ℤ_d, correctly summing fixed-point counts across all rotations. This handles prime, power-of-2, and composite d uniformly.

### Code restructuring

- **Moved** formula implementation from `Helpers/` to new `FormulaCode/` directory — the closed-form formula is a standalone feature, not a helper utility
- **Unified** the formula code: removed hardcoded per-diagonal methods (d=2, d=3) in favor of a single general `ComputeM_General` path that handles all d ≥ 2
- **Added** `BurnsideCyclicGroup()` method for correct A-coefficient computation on non-power-of-2 d

### Documentation

- **Renamed** `CLOSED_FORM_CONJECTURE_2SAT_MINUNSAT_DETAILED.md` → `CLOSED_FORM_FORMULA_PROOF_FOR_2SAT_MINUNSAT_DETAILED.md` to reflect proven status
- **Removed** old conjecture documents (`CLOSED_FORM_CONJECTURE_2SAT_MINUNSAT.md` and the detailed variant)
- **Updated** proof document to version 7.2 with closed proof gaps: inductive canonical uniqueness, Burnside integrality, exhaustive four-case enumeration, explicit GF(2) rank construction
- **Updated** README to reference the proof document and reflect the fully-proven status
