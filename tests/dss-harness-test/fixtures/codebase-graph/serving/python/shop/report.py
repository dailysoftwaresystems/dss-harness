"""Reports over shapes."""

from shop.shape import Shape


def describe(shape: Shape) -> str:
    """Report a shape for a person."""
    return f"{shape.name()} has area {shape.area():.2f}"


def table(shapes: list[Shape]) -> str:
    """A table of the shapes' areas."""
    lines = ""

    for shape in shapes:
        lines = lines + describe(shape) + "\n"

    return lines


def ruler(width: int) -> str:
    """A ruler of a width, marked each five columns."""
    marks = ""

    for at in range(width):
        if at % 10 == 0:
            marks = marks + "|"
        elif at % 5 == 0:
            marks = marks + "+"
        else:
            marks = marks + "-"

    return marks


def pad(text: str, width: int) -> str:
    """A text padded to a width, cut at its end where it is longer."""
    if len(text) >= width:
        return text[:width]

    return text + " " * (width - len(text))


def columns(left: list[str], right: list[str], width: int) -> str:
    """Two columns of texts side by side, each padded to its column's width."""
    lines = ""

    for at in range(max(len(left), len(right))):
        one = left[at] if at < len(left) else ""
        two = right[at] if at < len(right) else ""
        lines = lines + pad(one, width) + "  " + two + "\n"

    return lines


def roman(number: int) -> str:
    """A number in Roman numerals, as far as three thousands."""
    pairs = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")]
    left = number
    marks = ""

    for worth, mark in pairs:
        while left >= worth:
            marks = marks + mark
            left = left - worth

    return marks


def numbered(lines: list[str]) -> str:
    """A text with every line numbered from one."""
    text = ""
    at = 1

    for line in lines:
        text = text + str(at) + ". " + line + "\n"
        at = at + 1

    return text


def widest(lines: list[str]) -> int:
    """The widest line's length."""
    width = 0

    for line in lines:
        if len(line) > width:
            width = len(line)

    return width
