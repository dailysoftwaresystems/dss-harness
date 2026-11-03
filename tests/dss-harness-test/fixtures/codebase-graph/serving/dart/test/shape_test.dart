import 'package:test/test.dart';
import 'package:serving/geometry.dart';
import 'package:serving/report.dart';
import 'package:serving/shape.dart';

void main() {
  test('total adds the areas', () {
    expect(total([const Circle(1), const Square(2)]), closeTo(5.14159, 0.00001));
  });

  test('largest is the greatest area', () {
    expect(largest([const Circle(1), const Square(2)]), 4);
  });

  test('describe names the shape', () {
    expect(describe(const Circle(2)), startsWith('circle'));
  });
}
