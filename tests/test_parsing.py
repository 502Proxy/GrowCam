from growcam.domain.models import MilestoneType, SeedType, Severity
from growcam.infrastructure.claude_ai import parse_analysis, parse_seed

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))
import update  # noqa: E402


def test_parse_analysis_is_tolerant():
    result = parse_analysis(
        {
            "health_score": 77,
            "headline": " Gut ",
            "findings": [{"severity": "bogus", "title": "A", "advice": "B"}],
            "suggested_milestone": "flowering",
        }
    )
    assert result.headline == "Gut"
    assert result.findings[0].severity == Severity.MINOR
    assert result.suggested_milestone == MilestoneType.FLOWERING


def test_parse_analysis_none_milestone():
    assert parse_analysis({"suggested_milestone": "none"}).suggested_milestone is None


def test_parse_seed_unknown_type_and_extra_fields():
    seed = parse_seed({"name": "X", "seed_type": "weird", "breeder": "ignored"})
    assert seed.seed_type == SeedType.UNKNOWN


def test_updater_version_comparison():
    assert update.is_newer("v0.10.0", "v0.9.3")
    assert not update.is_newer("v0.2.0", "v0.2.0")
    assert update.is_newer("v0.1.0", "a1b2c3d")  # untagged checkout
    assert not update.is_newer("", "v0.1.0")
