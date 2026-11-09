"""Claude adapter for plant analysis, weekly reports and seed lookup.

Uses the Messages API directly through urllib (no SDK dependency). Structured
output is obtained by forcing a tool call with a JSON schema.
"""

from __future__ import annotations

import base64
import json
import urllib.error
import urllib.request
from typing import Any

from growcam.application.ports import AnalysisResult, AnalyzerError, GrowContext, ReportDraft
from growcam.domain.models import Finding, MilestoneType, SeedInfo, SeedType, Severity
from growcam.infrastructure.sqlite_store import seed_from_dict

API_URL = "https://api.anthropic.com/v1/messages"

STYLE_RULES = """\
You are the plant assistant of a small home grow-tent monitor.
Everything you write is shown to an older German reader with poor eyesight and
no gardening jargon: write in short, plain German sentences (du-Form), no
abbreviations, no Latin names, no numbers without units. Be calm and honest –
only report a problem when you actually see one. Never invent details you
cannot see in the photo."""

ANALYSIS_TOOL = {
    "name": "report_analysis",
    "description": "Report the result of looking at the plant photo.",
    "input_schema": {
        "type": "object",
        "properties": {
            "health_score": {"type": "integer", "minimum": 0, "maximum": 100},
            "headline": {
                "type": "string",
                "description": "One short sentence: how are the plants doing overall?",
            },
            "findings": {
                "type": "array",
                "maxItems": 3,
                "items": {
                    "type": "object",
                    "properties": {
                        "severity": {"type": "string", "enum": ["ok", "minor", "watch"]},
                        "title": {"type": "string", "description": "What you see, max. 8 words."},
                        "advice": {"type": "string", "description": "What to do, one or two short sentences."},
                    },
                    "required": ["severity", "title", "advice"],
                },
            },
            "suggested_milestone": {
                "type": "string",
                "enum": ["none", "sprouted", "vegetative", "flowering", "harvest"],
                "description": "Only if the photo clearly shows a stage listed as still open.",
            },
            "milestone_note": {"type": "string", "description": "What you saw that indicates the stage."},
        },
        "required": ["health_score", "headline", "findings", "suggested_milestone"],
    },
}

REPORT_TOOL = {
    "name": "report_week",
    "description": "Report the weekly summary.",
    "input_schema": {
        "type": "object",
        "properties": {
            "headline": {"type": "string", "description": "One sentence: how was the week?"},
            "happened": {"type": "array", "maxItems": 4, "items": {"type": "string"}},
            "next_steps": {"type": "array", "maxItems": 3, "items": {"type": "string"}},
            "needs_attention": {"type": "boolean"},
        },
        "required": ["headline", "happened", "next_steps", "needs_attention"],
    },
}

SEED_TOOL = {
    "name": "report_seeds",
    "description": "Report the strains that match the search, best match first.",
    "input_schema": {
        "type": "object",
        "properties": {
            "seeds": {
                "type": "array",
                "maxItems": 3,
                "items": {
                    "type": "object",
                    "properties": {
                        "name": {"type": "string", "description": "Strain name incl. breeder if known"},
                        "seed_type": {"type": "string", "enum": ["autoflower", "photoperiod", "unknown"]},
                        "weeks_to_harvest": {"type": ["integer", "null"], "description": "Autoflower: sowing to harvest"},
                        "flowering_weeks": {"type": ["integer", "null"], "description": "Photoperiod: flowering time"},
                        "height": {"type": "string", "description": "Indoor height, e.g. 'ca. 60–100 cm'"},
                        "light_schedule": {"type": "string", "description": "e.g. '18/6 die ganze Zeit'"},
                        "difficulty": {"type": "string", "description": "leicht / mittel / schwer"},
                    },
                    "required": ["name", "seed_type"],
                },
            }
        },
        "required": ["seeds"],
    },
}

MILESTONE_HINTS = {
    MilestoneType.SPROUTED: "sprouted = a seedling has broken through the soil",
    MilestoneType.VEGETATIVE: "vegetative = several true (serrated) leaf pairs, plant grows steadily",
    MilestoneType.FLOWERING: "flowering = first white pistils/hairs at the nodes or top",
    MilestoneType.HARVEST: "harvest = buds look mature, most pistils brown/curled",
}


class ClaudeClient:
    def __init__(self, api_key: str, model: str, api_version: str = "2023-06-01", timeout: float = 120) -> None:
        if not api_key:
            raise ValueError("ANTHROPIC_API_KEY is missing")
        self._api_key = api_key
        self._model = model
        self._api_version = api_version
        self._timeout = timeout

    def create(self, **payload: Any) -> dict:
        body = json.dumps({"model": self._model, **payload}).encode()
        request = urllib.request.Request(
            API_URL,
            data=body,
            headers={
                "x-api-key": self._api_key,
                "anthropic-version": self._api_version,
                "content-type": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                return json.load(response)
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode(errors="ignore")[:300]
            raise AnalyzerError(f"KI-Anfrage fehlgeschlagen ({exc.code}): {detail}") from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            raise AnalyzerError("Die KI ist gerade nicht erreichbar. Gibt es Internet?") from exc


def _image_block(data: bytes) -> dict:
    return {
        "type": "image",
        "source": {"type": "base64", "media_type": "image/jpeg", "data": base64.b64encode(data).decode()},
    }


def _tool_input(response: dict, tool_name: str) -> dict | None:
    for block in response.get("content", []):
        if block.get("type") == "tool_use" and block.get("name") == tool_name:
            return block.get("input", {})
    return None


def _describe(context: GrowContext) -> str:
    seed = context.seed
    lines = [
        f"Grow: {context.grow_name}, day {context.day_count} since sowing, current phase: {context.phase}.",
    ]
    if seed:
        lines.append(
            f"Strain: {seed.name} ({seed.seed_type.value}); "
            f"weeks to harvest: {seed.weeks_to_harvest or '?'}, flowering weeks: {seed.flowering_weeks or '?'}."
        )
    if context.open_milestones:
        lines.append("Stages not reached yet: " + "; ".join(MILESTONE_HINTS[m] for m in context.open_milestones))
    if context.recent_diary:
        lines.append("Recent diary: " + " | ".join(context.recent_diary))
    if context.previous_headline:
        lines.append(f"Previous assessment: {context.previous_headline}")
    if context.user_note:
        lines.append(f"Note from the grower: {context.user_note}")
    return "\n".join(lines)


class ClaudePlantAnalyzer:
    def __init__(self, client: ClaudeClient) -> None:
        self._client = client

    def analyze(self, image: bytes, previous_image: bytes | None, context: GrowContext) -> AnalysisResult:
        content: list[dict] = []
        if previous_image is not None:
            content += [{"type": "text", "text": "Photo from the previous check:"}, _image_block(previous_image)]
        content += [
            {"type": "text", "text": "Photo from now:"},
            _image_block(image),
            {
                "type": "text",
                "text": _describe(context)
                + "\n\nLook for deficiencies, pests, mould, light/heat stress, over- or underwatering, "
                "and compare with the previous photo if there is one. Then call report_analysis.",
            },
        ]
        response = self._client.create(
            max_tokens=1200,
            system=STYLE_RULES,
            tools=[ANALYSIS_TOOL],
            tool_choice={"type": "tool", "name": ANALYSIS_TOOL["name"]},
            messages=[{"role": "user", "content": content}],
        )
        data = _tool_input(response, ANALYSIS_TOOL["name"])
        if data is None:
            raise AnalyzerError("Die KI hat keine Auswertung geliefert.")
        return parse_analysis(data)

    def weekly_report(self, images: list[bytes], context: GrowContext, analyses: list[str]) -> ReportDraft:
        content: list[dict] = []
        for index, image in enumerate(images, start=1):
            content += [{"type": "text", "text": f"Day {index}:"}, _image_block(image)]
        content.append(
            {
                "type": "text",
                "text": _describe(context)
                + "\nDaily assessments this week: "
                + (" | ".join(analyses) or "none")
                + "\n\nSummarize the week for the grower and call report_week.",
            }
        )
        response = self._client.create(
            max_tokens=1200,
            system=STYLE_RULES,
            tools=[REPORT_TOOL],
            tool_choice={"type": "tool", "name": REPORT_TOOL["name"]},
            messages=[{"role": "user", "content": content}],
        )
        data = _tool_input(response, REPORT_TOOL["name"])
        if data is None:
            raise AnalyzerError("Die KI hat keinen Wochenbericht geliefert.")
        return ReportDraft(
            headline=data.get("headline", ""),
            happened=list(data.get("happened", [])),
            next_steps=list(data.get("next_steps", [])),
            needs_attention=bool(data.get("needs_attention", False)),
        )


class ClaudeSeedCatalog:
    """Looks up strain data with Claude's server-side web search."""

    MAX_ROUNDS = 4

    def __init__(self, client: ClaudeClient, max_searches: int = 3) -> None:
        self._client = client
        self._tools = [
            {"type": "web_search_20250305", "name": "web_search", "max_uses": max_searches},
            SEED_TOOL,
        ]

    def search(self, query: str) -> list[SeedInfo]:
        messages: list[dict] = [
            {
                "role": "user",
                "content": f"Find grow data for the cannabis seed strain '{query}' (indoor grow tent). "
                "Search the web (breeder or seed shop pages), then call report_seeds with up to three "
                "matching strains. Write height, light schedule and difficulty in short German.",
            }
        ]
        tool_choice: dict = {"type": "auto"}
        for _ in range(self.MAX_ROUNDS):
            response = self._client.create(
                max_tokens=2000, system=STYLE_RULES, tools=self._tools, tool_choice=tool_choice, messages=list(messages)
            )
            data = _tool_input(response, SEED_TOOL["name"])
            if data is not None:
                return [parse_seed(s) for s in data.get("seeds", []) if s.get("name")]
            # Paused server-side search or a plain text answer: continue the turn.
            messages.append({"role": "assistant", "content": response.get("content", [])})
            if response.get("stop_reason") != "pause_turn":
                messages.append({"role": "user", "content": "Bitte jetzt report_seeds aufrufen."})
                tool_choice = {"type": "tool", "name": SEED_TOOL["name"]}
        raise AnalyzerError("Die Sorten-Suche hat nichts geliefert.")


# ---------------------------------------------------------------------------
# Parsing (kept tolerant: models occasionally omit optional fields)
# ---------------------------------------------------------------------------


def parse_analysis(data: dict) -> AnalysisResult:
    findings = []
    for raw in data.get("findings", [])[:3]:
        try:
            severity = Severity(raw.get("severity", "minor"))
        except ValueError:
            severity = Severity.MINOR
        findings.append(Finding(severity, str(raw.get("title", "")).strip(), str(raw.get("advice", "")).strip()))
    milestone_raw = data.get("suggested_milestone", "none")
    try:
        milestone = None if milestone_raw in (None, "none") else MilestoneType(milestone_raw)
    except ValueError:
        milestone = None
    return AnalysisResult(
        health_score=int(data.get("health_score", 0)),
        headline=str(data.get("headline", "")).strip(),
        findings=findings,
        suggested_milestone=milestone,
        milestone_note=str(data.get("milestone_note", "")).strip(),
    )


def parse_seed(data: dict) -> SeedInfo:
    data = dict(data)
    if data.get("seed_type") not in {t.value for t in SeedType}:
        data["seed_type"] = SeedType.UNKNOWN.value
    return seed_from_dict(data)
