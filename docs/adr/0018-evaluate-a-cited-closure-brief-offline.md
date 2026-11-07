# 0018 Evaluate a cited closure brief offline

- Status: Accepted for offline evaluation
- Date: 2026-11-06

## Context

The closure handoff delivers a durable notice to the records queue. A clerk can inspect the selected case and its history to see the recorded transition. A draft can collect those observations, but a claim about when or why a case closed can mislead a reviewer if it comes from a baseline, mismatched case or user-entered text. The current workflow already provides the structured fields needed to test that boundary. [Closure handoff](../requirements/phase-10-case-closure-handoff.md)

## Decision

Evaluate a read-only closure brief before selecting a provider or integrating an assistant. Constrain the answer to three individually cited facts or a categorized abstention. Keep the request tied to one selected case. Use the authorized case and history reads as the prospective application evidence, and keep human review between a draft and its use.

Check in synthetic inputs with expected outcomes and score candidates offline. Keep a deterministic rules baseline that reads the evidence and applies the existing history contract. Report draft correctness and abstention correctness separately. The scorer compares exact citations and checks that abstention carries no facts. No network, model credentials or running API are needed for the evaluation. [Evaluation contract](../requirements/phase-11-closure-brief-evaluation.md)

## Consequences

The baseline sets a comparison point for any candidate. These structured facts are already recoverable through deterministic rules, so later AI work needs a concrete improvement for the clerk and independent synthetic evaluation. A candidate run can supply evidence for that decision. Authorization, selected-case lifetime and human review remain acceptance requirements for any proposed workspace integration.
