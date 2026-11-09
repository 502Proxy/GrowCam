from growcam.application.ports import GrowContext
from growcam.domain.models import MilestoneType, SeedType
from growcam.infrastructure.claude_ai import ClaudePlantAnalyzer, ClaudeSeedCatalog


class ScriptedClient:
    """Stands in for ClaudeClient and replays prepared responses."""

    def __init__(self, *responses):
        self.responses = list(responses)
        self.requests = []

    def create(self, **payload):
        self.requests.append(payload)
        return self.responses.pop(0)


def tool_use(name, data):
    return {"stop_reason": "tool_use", "content": [{"type": "tool_use", "name": name, "input": data}]}


CONTEXT = GrowContext("Test", 5, "Keimung", None, [MilestoneType.SPROUTED])


def test_analyzer_sends_both_images_and_forces_tool():
    client = ScriptedClient(tool_use("report_analysis", {"health_score": 90, "headline": "Gut", "findings": [], "suggested_milestone": "sprouted"}))
    result = ClaudePlantAnalyzer(client).analyze(b"now", b"before", CONTEXT)
    request = client.requests[0]
    assert request["tool_choice"] == {"type": "tool", "name": "report_analysis"}
    images = [b for b in request["messages"][0]["content"] if b["type"] == "image"]
    assert len(images) == 2
    assert result.suggested_milestone == MilestoneType.SPROUTED


def test_seed_search_continues_after_pause_and_text_answer():
    paused = {"stop_reason": "pause_turn", "content": [{"type": "server_tool_use", "name": "web_search", "input": {}}]}
    text_only = {"stop_reason": "end_turn", "content": [{"type": "text", "text": "Gefunden!"}]}
    final = tool_use("report_seeds", {"seeds": [{"name": "Northern Lights Auto", "seed_type": "autoflower", "weeks_to_harvest": 10}]})
    client = ScriptedClient(paused, text_only, final)

    seeds = ClaudeSeedCatalog(client).search("northern lights")

    assert seeds[0].seed_type == SeedType.AUTOFLOWER
    assert client.requests[2]["tool_choice"] == {"type": "tool", "name": "report_seeds"}
    # paused turn is sent back unchanged without an extra user message
    assert client.requests[1]["messages"][-1]["role"] == "assistant"
