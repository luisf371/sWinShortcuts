# Macro plan review log

> Historical planning record. The feature and editor at `f779947` subsequently passed hosted CI and independent code review. The current container/naming follow-up is tracked in `MACROS_PLAN.md`; the pre-implementation statements below describe their original review dates.

Scope: review and improve MACROS_PLAN.md before implementing the feature. Reviewers use fresh contexts, gpt-6-astra, xhigh reasoning, and the confirmed user requirements. Production code is unchanged by this review loop.

## Initial improvements

- Defined model defaults, supported step fields, structural validation, playable drafts, and resource limits.
- Added a concrete UI/service recording handoff, session modes, result ownership, and close/delete ordering.
- Specified strict malformed-INI handling that preserves original profile data.
- Clarified recording row-budget expansion, including balancing releases and the destination's existing rows.

## Review rounds

| Round | Reviewed SHA256 | Result |
|---|---|---|
| 1 | 4AB66C8A4FEA79F6B7B8798ECAC428CE5C53E04E8586EF1F2A7308B02FE63D65 | 1 High, 3 Medium; corrected before round 2 |
| 2 | 5AEDBCCB8E8AB37A345A126EC47142C22D94B1B042FFBDF3F63C14A47D9593CA | 2 Medium; corrected before round 3 |
| 3 | D9CA36A561C9BA65099559CEC70BF8F9ACAF081828E18D227FCE6C895022499A | 0 actionable findings; ready for implementation |

### Round 1 corrections

- Physical takeover now covers ordinary queued releases, failed-release retries, and cleanup, with retained origin and a tagged-release hook check for the final native-call race. A previously suppressed physical pair is not treated as a replacement hold.
- Strict macro loading includes minimal IniDocument section/source-value presence support. Empty, orphaned, or extra declared macro data cannot be mistaken for a legacy profile and overwritten.
- Shortcut assignment uses a key picker and modifier checkboxes. This meets the requested assignment capability without another capture mode competing with existing global hooks.
- Runtime and editor shortcut conflicts refresh through global toggle setters, settings rollback/hydration, and profile/Launcher binding changes, using each feature's actual matching rule.
- Additional local checks made both macro enable controls explicit, retained X-button mouseData in recordings, specified asynchronous pre-close finalization, and distinguished SendInput insertion from observed cursor arrival with bounded clipped-cursor failure handling.

The full independent round 1 report is retained in `.tmp/macro-plan-review-r1.md`.

### Round 2 findings and corrections

- A busy Ctrl+F6 activation begins with a separate Ctrl DOWN, so physical modifiers now defer new output without cancelling or swallowing Alt-Tab. Owed releases proceed, a deferral is distinct from native failure, and a still-intended macro modifier is reasserted after overlapping physical input releases. The full second-chord dispatch, queued admission, and overlap cases are planned regressions. Real modifiers can still affect already-held inputs, which is explicitly disclosed.
- Other features can synthesize modifiers whose physical source key is different. The plan now reserves modifier admission through the executor, rejects pre-existing foreign modifier holds/releases, and prevents new foreign modifier DOWNs while retaining their UP obligations and intentional Macro modifiers.

The full independent round 2 report is retained in `.tmp/macro-plan-review-r2.md`.

The round 2 reviewer also checked the proposed correction in a bounded follow-up and confirmed that deferral plus executor reservation addresses both findings, provided intended macro modifiers are restored after physical overlap and reservation is checked at delivery. Round 3 then evaluated the revised plan independently.

### Final verdict and verification

The third fresh-context gpt-6-astra/xhigh reviewer returned **Ready for implementation, with zero supported actionable findings**. It checked the current source and did not access previous reports. The final plan hash matched before and after review and was unchanged during closeout. Across the three rounds, six supported findings were corrected: one High and five Medium.

- [Final independent review](.tmp/macro-plan-review-r3.md)
- [Reviewed implementation plan](MACROS_PLAN.md)
- Document validation: seven implementation phases, balanced Markdown fences, no trailing whitespace or conflict markers, and git diff --check passed.
- Only MACROS_PLAN.md, this review log, and append-only memory.md notes changed outside ignored local review reports. No production implementation, native input testing, build, branch, commit, or PR was performed during the plan loop.

Implementation and its planned automated/manual validation remain future work. Commit and PR delivery remain authorized after that implementation is complete; merging is outside scope.

## Completion criteria

Each supported finding is checked against the repository and corrected in the plan. After revisions, a new reviewer receives the current plan and user requirements without prior review reports. The loop ends only when a fresh review returns zero supported actionable gaps and local document checks pass for that same plan content.

## Implementation-stage scope update — 2026-09-13

The user reduced the macro limit to 1,000 steps during code review. MACROS_PLAN.md now uses that limit for saved definitions and recording finalization; all other approved scope remains. The earlier reviewed hash documents the original planning pass. Subsequent independent full-feature code reviews receive this updated plan and the 1,000-step requirement.

## 2026-09-13 implementation-stage mouse behavior update

The user requested per-macro cancellation on physical mouse movement to be optional and default off, and more useful debug logging. The current plan reflects that setting throughout playback, its legacy-INI default, and continued exact-arrival/input-ownership checks. A native reproduction of the user's cursor-arrival error found one-pixel rounding when the final absolute send ran outside per-monitor DPI awareness; the plan now includes the scoped native-send context. The original planning verdict remains historical. A fresh full-feature code review will follow green CI on these implementation changes.

User testing follow-up (2026-09-13): increased the shared cursor speed from 3,000 to 9,000 physical pixels/second. The point spacing now derives from the same cap and existing 8 ms interval; no settings UI or per-step speed field was requested. All saved waits/holds and movement validation remain. MACROS_PLAN.md reflects this approved scope update; the original planning review remains historical.

## 2026-09-14 implementation follow-up

The UI container and naming changes are implementation-stage follow-ups: preserve their returned work before any root corrections, then run exact-head CI and a fresh independent code review. The returned naming stage builds without warnings and passes 100 focused checks, including the original minimum-size layout assertion. This checkpoint is not the final hosted-CI or independent-review verdict.

The shared recorder now excludes repeated already-held modifier DOWNs without changing elapsed capture timing or ordinary typematic. Eleven modifier variants reproduced the defect before the fix. The isolated runtime-only build passed 1,370 tests with two existing desktop skips; the integrated UI build is validated separately.
