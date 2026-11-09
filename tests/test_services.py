from datetime import date, datetime, timedelta

import pytest

from growcam.application.services import NoActiveGrowError, summarize_diary
from growcam.domain.models import DiaryEntry, DiaryKind, MilestoneStatus, MilestoneType, Phase, SeedInfo


def start(stack, sow=date(2026, 10, 20)):
    return stack.grows.start("Herbst", sow, 3, SeedInfo("Northern Lights"))


def test_analysis_requires_running_grow(stack):
    with pytest.raises(NoActiveGrowError):
        stack.monitoring.analyze_now()


def test_starting_a_grow_archives_the_previous_one(stack):
    first = start(stack)
    second = stack.grows.start("Winter", date(2026, 11, 9), 1, None)
    assert stack.grows.active().id == second.id
    assert [g.id for g in stack.grows.archived()] == [first.id]


def test_analysis_is_stored_clamped_and_trimmed(stack):
    start(stack)
    analysis = stack.monitoring.analyze_now("neuer Dünger")
    assert analysis.health_score == 100
    assert len(analysis.findings) == 3
    assert stack.analyzer.contexts[0].user_note == "neuer Dünger"
    assert stack.analyzer.contexts[0].day_count == 20


def test_ai_suggestion_waits_for_user_confirmation(stack):
    start(stack)
    stack.analyzer.suggest = MilestoneType.SPROUTED
    stack.monitoring.analyze_now()
    grow = stack.grows.active()
    assert grow.pending_suggestion().type == MilestoneType.SPROUTED
    assert grow.phase() == Phase.GERMINATION

    stack.grows.confirm_milestone(MilestoneType.SPROUTED)
    assert stack.grows.active().phase() == Phase.SEEDLING


def test_rejected_suggestion_has_cooldown(stack):
    start(stack)
    stack.analyzer.suggest = MilestoneType.FLOWERING
    stack.monitoring.analyze_now()
    stack.grows.reject_milestone(MilestoneType.FLOWERING)

    stack.clock.current += timedelta(days=1)
    stack.monitoring.analyze_now()
    assert stack.grows.active().milestone(MilestoneType.FLOWERING).status == MilestoneStatus.REJECTED

    stack.clock.current += timedelta(days=2)
    stack.monitoring.analyze_now()
    assert stack.grows.active().milestone(MilestoneType.FLOWERING).status == MilestoneStatus.SUGGESTED


def test_confirming_later_milestone_implies_earlier_ones(stack):
    start(stack)
    stack.grows.confirm_milestone(MilestoneType.VEGETATIVE)
    grow = stack.grows.active()
    assert grow.is_reached(MilestoneType.SPROUTED)
    assert grow.phase() == Phase.VEGETATIVE


def test_weekly_report_uses_one_photo_per_day_and_diary_counts(stack):
    start(stack)
    monday = date(2026, 11, 2)
    for day in range(7):
        for hour in (9, 12, 18):
            stack.clock.current = datetime.combine(monday + timedelta(days=day), datetime.min.time()).replace(hour=hour)
            stack.monitoring.take_snapshot(scheduled=True)
    stack.grows.add_diary_entry(DiaryKind.WATERED)
    stack.grows.add_diary_entry(DiaryKind.WATERED)
    stack.grows.add_diary_entry(DiaryKind.NOTE, "Lampe höher")

    stack.clock.current = datetime(2026, 11, 8, 20, 0)  # Sunday evening
    report = stack.reports.generate_last_week_if_missing()
    assert report is not None
    assert len(report.snapshot_ids) == 7
    assert len(stack.analyzer.report_images) == 7
    assert report.done == ["2× gegossen", "Lampe höher"]
    # second call for the same week does nothing
    assert stack.reports.generate_last_week_if_missing() is None


def test_remembered_camera_position(stack):
    preset = stack.camera.remember_analysis_position()
    assert stack.camera.analysis_preset() == preset


def test_summarize_diary_ignores_empty_notes():
    entries = [DiaryEntry(None, 1, datetime(2026, 1, 1), DiaryKind.NOTE, "")]
    assert summarize_diary(entries) == []
