# Closure brief request routing experiment

Keep the explicit closure brief request. The tested local model recognized supported wording that the phrase baseline missed, but accepted 12 of 14 out-of-scope requests. [Phase 11 evaluation contract](../requirements/phase-11-closure-brief-evaluation.md)

## Question and gate

Can a local model recognize varied wording for the same read-only brief while refusing requests outside that task? A router chooses `closure_brief` or `unsupported_request`. The rules from Phase 11 still inspect the selected case and history, determine whether to draft or abstain, and construct every fact and citation. The classifier receives only synthetic question text. The model has no case evidence or action interface.

The experiment uses 30 new cases separate from the 19 Phase 11 examples. Labels, the prompt policy and the comparison were frozen in commit `44491e3` before inference. The test set has 16 supported prompts, including six that still require abstention because of the evidence, and 14 unsupported prompts. Unsupported prompts include requests for reasons, approval, downstream receipts, other cases, writes, mixed intent and instructions to force the label. [Prompt fixture](../../tests/fixtures/closure-brief-routing/prompts.json) · [Case evidence and answer key](../../tests/fixtures/closure-brief-routing/cases.json)

To clear the gate, the candidate must correctly route all 30 requests and match all downstream answers, including citations and abstention reasons. Missing, duplicate or malformed decisions fail the gate and fall back to the unsupported route for answer scoring. The phrase comparison normalizes case, spacing and surrounding punctuation, then matches whole phrases from a fixed list. The explicit-request comparison uses the correct typed request for each case to establish what the rules can do once routing is settled.

## Observed results

| Routing input | Supported routed | Unsupported rejected | Correct drafts | Correct abstentions | Gate |
| --- | ---: | ---: | ---: | ---: | --- |
| Explicit typed request | 16/16 | 14/14 | 10/10 | 20/20 | Pass |
| Exact phrase router | 4/16 | 14/14 | 3/10 | 15/20 | Fail |
| Local Llama 3.1 8B Q4_K_M | 16/16 | 2/14 | 10/10 | 8/20 | Fail |

Two local runs with the same model and settings returned identical labels on this test set. The model passed all six evidence-driven abstentions through to the rules. Its 12 false accepts instead presented a closure brief in response to a different request. Among them were a request to change case status and an instruction to force the classifier to choose the brief. The output schema constrained the label shape. The holdout measured whether the labels were correct. [Recorded model labels](../../tests/fixtures/closure-brief-routing/observed-model.json)

These are results for one prompt policy, one model snapshot and one synthetic set. Any revised candidate needs new synthetic cases for a fresh acceptance run. The workspace follows this result with an explicit **View closure brief** action. [Workspace contract](../requirements/phase-11-closure-brief-evaluation.md#workspace-and-http)

## Reproduce

Run from the repository root. Scoring the recorded output needs the pinned .NET 10 SDK. The phrase comparison and optional repeat of inference use Python 3 with its standard library. The repeat uses a local Ollama server on `127.0.0.1:11434` and the installed `llama3.1:latest` digest pinned in the script. The recorded run used Ollama 0.30.7, temperature 0, seed 42 and a two-label JSON schema. [Ollama structured output](https://docs.ollama.com/capabilities/structured-outputs) · [Ollama model catalog](https://ollama.com/library/llama3.1)

```sh
ROUTE_CASES=tests/fixtures/closure-brief-routing/cases.json
ROUTE_PROMPTS=tests/fixtures/closure-brief-routing/prompts.json
ROUTE_RESULTS="$(mktemp -d)"
ROUTE_MODEL_RECORD=tests/fixtures/closure-brief-routing/observed-model.json

dotnet run --file tools/ClosureBriefEvaluation.cs --configuration Release -- --baseline "$ROUTE_CASES"

python3 tools/ClosureBriefRouting.py phrase "$ROUTE_PROMPTS" > "$ROUTE_RESULTS/phrase.json"
dotnet run --file tools/ClosureBriefEvaluation.cs --configuration Release -- \
  --route-score "$ROUTE_CASES" "$ROUTE_RESULTS/phrase.json"

dotnet run --file tools/ClosureBriefEvaluation.cs --configuration Release -- \
  --route-score "$ROUTE_CASES" "$ROUTE_MODEL_RECORD"
```

The phrase and recorded model commands are expected to report gate failure with exit 1. Exit 0 means every route and answer matched. Exit 2 indicates invalid input.

To repeat local inference and score the new predictions, run the following with the same variables.

```sh
python3 tools/ClosureBriefRouting.py model "$ROUTE_PROMPTS" > "$ROUTE_RESULTS/model.json"
dotnet run --file tools/ClosureBriefEvaluation.cs --configuration Release -- \
  --route-score "$ROUTE_CASES" "$ROUTE_RESULTS/model.json"
```
