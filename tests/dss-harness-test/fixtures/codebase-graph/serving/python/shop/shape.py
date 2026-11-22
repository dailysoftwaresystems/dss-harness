"""Shapes with an area."""


class Shape:
    """A thing with an area."""

    def area(self) -> float:
        """The shape's area."""
        return 0

    def name(self) -> str:
        """The shape's name."""
        return "shape"


class Circle(Shape):
    """A circle."""

    def __init__(self, radius: float) -> None:
        self.radius = radius

    def area(self) -> float:
        return 3.14159 * self.radius * self.radius

    def name(self) -> str:
        return f"circle of radius {self.radius}"


class Square(Shape):
    """A square."""

    def __init__(self, side: float) -> None:
        self.side = side

    def area(self) -> float:
        return self.side * self.side

    def name(self) -> str:
        return f"square of side {self.side}"


class Triangle(Shape):
    """A triangle by its three sides."""

    def __init__(self, a: float, b: float, c: float) -> None:
        self.a = a
        self.b = b
        self.c = c

    def area(self) -> float:
        half = (self.a + self.b + self.c) / 2
        return sqrt(half * (half - self.a) * (half - self.b) * (half - self.c))

    def name(self) -> str:
        return f"triangle of sides {self.a}, {self.b}, {self.c}"


def sqrt(value: float) -> float:
    """A square root by Newton's method, where the value is positive; else zero."""
    if value <= 0:
        return 0

    guess = value / 2

    for step in range(40):
        if abs(guess * guess - value) < 1e-12:
            return guess

        guess = (guess + value / guess) / 2

    return guess
