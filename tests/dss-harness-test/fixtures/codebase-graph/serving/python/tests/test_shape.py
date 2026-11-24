"""The shapes' tests."""

from shop.geometry import largest, total
from shop.report import describe
from shop.shape import Circle, Square


def shapes() -> list:
    """A circle and a square."""
    return [Circle(1), Square(2)]


def test_total_adds_the_areas() -> None:
    assert total(shapes()) == 5.14159


def test_largest_is_the_greatest_area() -> None:
    assert largest(shapes()) == 4


def test_describe_names_the_shape() -> None:
    assert describe(Circle(2))[:6] == "circle"
