from datetime import date

from growcam.domain.models import Grow, Milestone, MilestoneStatus, MilestoneType, Phase, SeedInfo, SeedType


def make_grow(**kwargs) -> Grow:
    return Grow(id=1, name="Test", sow_date=date(2026, 10, 1), plant_count=2, **kwargs)


def test_new_grow_is_germinating():
    grow = make_grow()
    assert grow.phase() == Phase.GERMINATION
    assert grow.day_count(date(2026, 10, 11)) == 10


def test_phase_follows_latest_confirmed_milestone_only():
    grow = make_grow(
        milestones=[
            Milestone(MilestoneType.SPROUTED, MilestoneStatus.CONFIRMED, date(2026, 10, 5)),
            Milestone(MilestoneType.VEGETATIVE, MilestoneStatus.SUGGESTED, date(2026, 10, 15)),
        ]
    )
    assert grow.phase() == Phase.SEEDLING
    assert grow.pending_suggestion().type == MilestoneType.VEGETATIVE
    assert MilestoneType.SPROUTED not in grow.open_milestones()


def test_autoflower_expected_harvest_uses_seed_data():
    grow = make_grow(seed=SeedInfo("Auto", SeedType.AUTOFLOWER, weeks_to_harvest=10))
    assert grow.expected_date(MilestoneType.HARVEST) == date(2026, 12, 10)


def test_photoperiod_harvest_unknown_until_flowering_confirmed():
    grow = make_grow(seed=SeedInfo("Photo", SeedType.PHOTOPERIOD, flowering_weeks=8))
    assert grow.expected_date(MilestoneType.HARVEST) is None
    grow.milestones.append(Milestone(MilestoneType.FLOWERING, MilestoneStatus.CONFIRMED, date(2026, 11, 20)))
    assert grow.expected_date(MilestoneType.HARVEST) == date(2027, 1, 15)
