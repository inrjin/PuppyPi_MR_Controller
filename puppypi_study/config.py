"""Validated, portable settings for the inspection-framing experiment.

Distances are image fractions, areas are fractions of the whole image and all
duration settings are seconds. The demo values are not a confirmed study plan.
"""

import hashlib
import json
import math
from dataclasses import dataclass, fields, replace
from pathlib import Path
from types import MappingProxyType
from typing import Any, Dict, Mapping, Tuple


INTERFACE_DEFINITIONS = MappingProxyType({
    "smartphone_touch": "Smartphone two-axis touch joystick (comparison condition; app not implemented here)",
    "mr_ballgrip": "Quest passthrough and hand-tracked BallGrip",
})


def seconds_to_us(value: float) -> int:
    """Round duration requirements up to a whole microsecond."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("duration must be a finite number of seconds")
    if value < 0 or value > (2**63 - 1) / 1_000_000:
        raise ValueError("duration must be finite, nonnegative and fit int64 microseconds")
    scaled = value * 1_000_000
    if not math.isfinite(scaled) or scaled > 2**63 - 1:
        raise ValueError("duration must be finite, nonnegative and fit int64 microseconds")
    return math.ceil(scaled)


def _number(name: str, value: Any, minimum: float, maximum: float) -> None:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(name + " must be finite and numeric")
    if not minimum <= value <= maximum:
        raise ValueError(name + " is outside its allowed range")
    if not math.isfinite(value):
        raise ValueError(name + " must be finite and numeric")


def _text(name: str, value: Any) -> None:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(name + " must be a nonempty string")


def _mapping(value: Any, label: str) -> Mapping:
    if not isinstance(value, Mapping):
        raise ValueError(label + " must be an object")
    return value


def _known_keys(data: Mapping, allowed: set, label: str) -> None:
    unknown = set(data) - allowed
    if unknown:
        raise ValueError(label + " has unknown keys: " + ", ".join(sorted(unknown)))


@dataclass(frozen=True)
class FramingConfig:
    target_marker: str
    center_tolerance_x: float
    center_tolerance_y: float
    area_ratio_min: float
    area_ratio_max: float
    hold_seconds: float
    max_frame_age: float
    max_frame_gap: float
    time_limit: float
    require_neutral: bool = False
    neutral_threshold: float = 0.02

    def __post_init__(self) -> None:
        _text("target_marker", self.target_marker)
        _number("center_tolerance_x", self.center_tolerance_x, 0, 0.5)
        _number("center_tolerance_y", self.center_tolerance_y, 0, 0.5)
        _number("area_ratio_min", self.area_ratio_min, 0, 1)
        _number("area_ratio_max", self.area_ratio_max, 0, 1)
        if not 0 < self.area_ratio_min <= self.area_ratio_max:
            raise ValueError("area ratios must satisfy 0 < min <= max <= 1")
        for name in ("hold_seconds", "max_frame_age", "max_frame_gap", "time_limit"):
            if seconds_to_us(getattr(self, name)) <= 0:
                raise ValueError(name + " must be positive")
        if self.hold_us >= self.time_limit_us:
            raise ValueError("hold_seconds must be shorter than time_limit")
        if not isinstance(self.require_neutral, bool):
            raise ValueError("require_neutral must be a boolean")
        _number("neutral_threshold", self.neutral_threshold, 0, 1)

    @property
    def hold_us(self) -> int:
        return seconds_to_us(self.hold_seconds)

    @property
    def max_frame_age_us(self) -> int:
        return seconds_to_us(self.max_frame_age)

    @property
    def max_frame_gap_us(self) -> int:
        return seconds_to_us(self.max_frame_gap)

    @property
    def time_limit_us(self) -> int:
        return seconds_to_us(self.time_limit)

    def to_dict(self) -> Dict[str, Any]:
        return {field.name: getattr(self, field.name) for field in fields(self)}

    @classmethod
    def from_dict(cls, data: Mapping) -> "FramingConfig":
        data = _mapping(data, "framing")
        _known_keys(data, {field.name for field in fields(cls)}, "framing")
        try:
            return cls(**data)
        except TypeError as error:
            raise ValueError("missing or invalid framing settings: " + str(error)) from error


@dataclass(frozen=True)
class StudyConfig:
    schema_version: int
    config_version: str
    profile_status: str
    interface_order: Tuple[str, ...]
    framing: FramingConfig
    difficulties: Mapping[str, Mapping[str, float]]
    implemented_interfaces: Tuple[str, ...] = ("mr_ballgrip",)

    def __post_init__(self) -> None:
        if type(self.schema_version) is not int or self.schema_version != 1:
            raise ValueError("unsupported config schema_version")
        _text("config_version", self.config_version)
        if self.profile_status not in ("demo", "pilot", "confirmed"):
            raise ValueError("profile_status must be demo, pilot or confirmed")
        if not isinstance(self.interface_order, (tuple, list)) or not self.interface_order:
            raise ValueError("interface_order must contain canonical interface IDs")
        if any(not isinstance(value, str) or value not in INTERFACE_DEFINITIONS for value in self.interface_order):
            raise ValueError("interface_order contains an unknown interface ID")
        if len(set(self.interface_order)) != len(self.interface_order):
            raise ValueError("interface_order may not contain duplicate IDs")
        if not isinstance(self.implemented_interfaces, (tuple, list)) or tuple(self.implemented_interfaces) != ("mr_ballgrip",):
            raise ValueError("only the mr_ballgrip client is implemented in this scope")
        if not isinstance(self.framing, FramingConfig):
            raise ValueError("framing must be FramingConfig")
        _mapping(self.difficulties, "difficulties")
        if not self.difficulties:
            raise ValueError("at least one difficulty is required")
        copied = {}
        for difficulty_id, overrides in self.difficulties.items():
            _text("difficulty_id", difficulty_id)
            overrides = _mapping(overrides, "difficulty")
            if set(overrides) != {"center_tolerance_x"}:
                raise ValueError("difficulties may override only center_tolerance_x")
            replace(self.framing, **overrides)
            copied[difficulty_id] = MappingProxyType(dict(overrides))
        # Detach caller-owned dictionaries before they become a trial snapshot.
        object.__setattr__(self, "interface_order", tuple(self.interface_order))
        object.__setattr__(self, "implemented_interfaces", tuple(self.implemented_interfaces))
        object.__setattr__(self, "difficulties", MappingProxyType(copied))

    def profile(self, difficulty_id: str) -> FramingConfig:
        if difficulty_id not in self.difficulties:
            raise ValueError("unknown difficulty_id: " + str(difficulty_id))
        return replace(self.framing, **self.difficulties[difficulty_id])

    def to_dict(self) -> Dict[str, Any]:
        return {
            "schema_version": self.schema_version,
            "config_version": self.config_version,
            "profile_status": self.profile_status,
            "interface_order": list(self.interface_order),
            "implemented_interfaces": list(self.implemented_interfaces),
            "framing": self.framing.to_dict(),
            "difficulties": {key: dict(value) for key, value in self.difficulties.items()},
        }

    @property
    def sha256(self) -> str:
        encoded = json.dumps(self.to_dict(), sort_keys=True, separators=(",", ":"), allow_nan=False)
        return hashlib.sha256(encoded.encode("utf-8")).hexdigest()

    @classmethod
    def from_dict(cls, data: Mapping) -> "StudyConfig":
        data = _mapping(data, "config")
        _known_keys(data, {field.name for field in fields(cls)}, "config")
        try:
            return cls(
                schema_version=data["schema_version"],
                config_version=data["config_version"],
                profile_status=data["profile_status"],
                interface_order=data["interface_order"],
                framing=FramingConfig.from_dict(data["framing"]),
                difficulties=data["difficulties"],
                implemented_interfaces=data.get("implemented_interfaces", ("mr_ballgrip",)),
            )
        except KeyError as error:
            raise ValueError("missing config setting: " + str(error)) from error


def _unique_object(pairs: list) -> Dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key: " + key)
        result[key] = value
    return result


def load_config(path: Any) -> StudyConfig:
    with Path(path).open("r", encoding="utf-8") as stream:
        return StudyConfig.from_dict(json.load(stream, object_pairs_hook=_unique_object))


def demo_config() -> StudyConfig:
    return StudyConfig.from_dict({
        "schema_version": 1,
        "config_version": "demo-v1",
        "profile_status": "demo",
        "interface_order": ["smartphone_touch", "mr_ballgrip"],
        "implemented_interfaces": ["mr_ballgrip"],
        "framing": {
            "target_marker": "KHCI2027:inspection",
            "center_tolerance_x": 0.12,
            "center_tolerance_y": 0.15,
            "area_ratio_min": 0.02,
            "area_ratio_max": 0.20,
            "hold_seconds": 1.0,
            "max_frame_age": 0.3,
            "max_frame_gap": 0.3,
            "time_limit": 60.0,
            "require_neutral": False,
            "neutral_threshold": 0.02,
        },
        "difficulties": {
            "demo-wide": {"center_tolerance_x": 0.12},
            "demo-narrow": {"center_tolerance_x": 0.04},
        },
    })
