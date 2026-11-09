"""Offline stand-ins for the AI, used when no API key is configured."""

from __future__ import annotations

from growcam.application.ports import AnalysisResult, GrowContext, ReportDraft
from growcam.domain.models import Finding, MilestoneType, SeedInfo, SeedType, Severity

# Rough day thresholds at which the demo "detects" a stage.
_DEMO_STAGE_DAYS = [
    (MilestoneType.SPROUTED, 3),
    (MilestoneType.VEGETATIVE, 14),
    (MilestoneType.FLOWERING, 40),
]


class DemoPlantAnalyzer:
    def analyze(self, image: bytes, previous_image: bytes | None, context: GrowContext) -> AnalysisResult:
        suggestion = next(
            (kind for kind, day in _DEMO_STAGE_DAYS if kind in context.open_milestones and context.day_count >= day),
            None,
        )
        return AnalysisResult(
            health_score=84,
            headline="Den Pflanzen geht es gut – eine Kleinigkeit im Auge behalten.",
            findings=[
                Finding(Severity.WATCH, "Untere Blätter werden etwas heller", "Düngung prüfen und in zwei bis drei Tagen noch mal vergleichen."),
                Finding(Severity.OK, "Neue Blätter sind sattgrün", "Nichts zu tun."),
            ],
            suggested_milestone=suggestion,
            milestone_note="(Demo) Die KI glaubt, diese Phase auf dem Foto zu erkennen." if suggestion else "",
        )

    def weekly_report(self, images: list[bytes], context: GrowContext, analyses: list[str]) -> ReportDraft:
        return ReportDraft(
            headline="Ruhige Woche, die Pflanzen wachsen gleichmäßig.",
            happened=["Viele neue Blätter sind dazugekommen.", "Keine Schädlinge oder Schimmel zu sehen."],
            next_steps=["Weiter wie bisher gießen.", "Untere Blätter im Auge behalten."],
            needs_attention=False,
        )


_DEMO_SEEDS = [
    SeedInfo("Northern Lights Auto", SeedType.AUTOFLOWER, 11, None, "ca. 60–100 cm", "18/6 die ganze Zeit", "leicht"),
    SeedInfo("Gorilla Glue #4", SeedType.PHOTOPERIOD, None, 9, "ca. 100–150 cm", "Wachstum 18/6, Blüte 12/12", "mittel"),
    SeedInfo("Amnesia Haze", SeedType.PHOTOPERIOD, None, 11, "ca. 120–180 cm", "Wachstum 18/6, Blüte 12/12", "mittel bis schwer"),
]


class DemoSeedCatalog:
    def search(self, query: str) -> list[SeedInfo]:
        q = query.lower()
        return [s for s in _DEMO_SEEDS if any(word in s.name.lower() for word in q.split())] or list(_DEMO_SEEDS)
