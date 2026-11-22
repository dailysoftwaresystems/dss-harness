"""Work over many shapes."""

from shop.shape import Shape


def total(shapes: list[Shape]) -> float:
    """The total of the shapes' areas."""
    return sum(shape.area() for shape in shapes)


def largest(shapes: list[Shape]) -> float:
    """The largest of the shapes' areas."""
    biggest = 0

    for shape in shapes:
        if shape.area() > biggest:
            biggest = shape.area()

    return biggest


def count(shapes: list[Shape]) -> int:
    """How many shapes there are."""
    return len(shapes)


def mean(shapes: list[Shape]) -> float:
    """The shapes' mean area."""
    return total(shapes) / count(shapes)


def median(shapes: list[Shape]) -> float:
    """The median of the shapes' areas."""
    return sorted(shape.area() for shape in shapes)[count(shapes) // 2]


def smallest(shapes: list[Shape]) -> float:
    """The smallest of the shapes' areas."""
    return min(shape.area() for shape in shapes)


def span(shapes: list[Shape]) -> float:
    """The span of the shapes' areas, largest to smallest."""
    return largest(shapes) - smallest(shapes)


def past(shapes: list[Shape], bound: float) -> list[Shape]:
    """Every shape whose area is past a bound."""
    return [shape for shape in shapes if shape.area() > bound]


def within(shapes: list[Shape], bound: float) -> list[Shape]:
    """Every shape whose area is within a bound."""
    return [shape for shape in shapes if shape.area() <= bound]
