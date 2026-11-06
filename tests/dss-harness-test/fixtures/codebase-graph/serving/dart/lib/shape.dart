/// A thing with an area.
abstract interface class Shape {
  /// The shape's area.
  double area();

  /// The shape's name.
  String name();
}

/// A circle.
class Circle implements Shape {
  /// The circle's radius.
  final double radius;

  /// A circle of [radius].
  const Circle(this.radius);

  @override
  double area() => 3.14159 * radius * radius;

  @override
  String name() => 'circle of radius $radius';
}

/// A square.
class Square implements Shape {
  /// The square's side.
  final double side;

  /// A square of [side].
  const Square(this.side);

  @override
  double area() => side * side;

  @override
  String name() => 'square of side $side';
}

/// A triangle by its three sides.
class Triangle implements Shape {
  /// The triangle's first side.
  final double a;

  /// The triangle's second side.
  final double b;

  /// The triangle's third side.
  final double c;

  /// A triangle of [a], [b] and [c].
  const Triangle(this.a, this.b, this.c);

  @override
  double area() {
    final s = (a + b + c) / 2;
    return _sqrt(s * (s - a) * (s - b) * (s - c));
  }

  @override
  String name() => 'triangle of sides $a, $b, $c';

  static double _sqrt(double value) => value <= 0 ? 0 : _byNewton(value, value / 2, 0);

  static double _byNewton(double value, double guess, int step) =>
      step > 40 || (guess * guess - value).abs() < 1e-12 ? guess : _byNewton(value, (guess + value / guess) / 2, step + 1);
}

/// A regular pentagon.
class Pentagon implements Shape {
  /// The pentagon's side.
  final double side;

  /// A pentagon of [side].
  const Pentagon(this.side);

  @override
  double area() => 0.25 * _sqrt(5 * (5 + 2 * _sqrt(5))) * side * side;

  @override
  String name() => 'pentagon of side $side';

  static double _sqrt(double value) => value <= 0 ? 0 : _byNewton(value, value / 2, 0);

  static double _byNewton(double value, double guess, int step) =>
      step > 40 || (guess * guess - value).abs() < 1e-12 ? guess : _byNewton(value, (guess + value / guess) / 2, step + 1);
}

/// A regular hexagon.
class Hexagon implements Shape {
  /// The hexagon's side.
  final double side;

  /// A hexagon of [side].
  const Hexagon(this.side);

  @override
  double area() => 1.5 * _sqrt(3) * side * side;

  @override
  String name() => 'hexagon of side $side';

  static double _sqrt(double value) => value <= 0 ? 0 : _byNewton(value, value / 2, 0);

  static double _byNewton(double value, double guess, int step) =>
      step > 40 || (guess * guess - value).abs() < 1e-12 ? guess : _byNewton(value, (guess + value / guess) / 2, step + 1);
}

/// A rectangle.
class Rectangle implements Shape {
  /// The rectangle's width.
  final double width;

  /// The rectangle's height.
  final double height;

  /// A rectangle of [width] by [height].
  const Rectangle(this.width, this.height);

  @override
  double area() => width * height;

  @override
  String name() => 'rectangle of $width by $height';
}

/// A ring between two circles.
class Ring implements Shape {
  /// The ring's inner radius.
  final double inner;

  /// The ring's outer radius.
  final double outer;

  /// A ring of [inner] inside [outer].
  const Ring(this.inner, this.outer);

  @override
  double area() => 3.14159 * (outer * outer - inner * inner);

  @override
  String name() => 'ring of $inner inside $outer';
}
