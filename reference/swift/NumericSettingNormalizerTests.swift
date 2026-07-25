import XCTest
@testable import JamCon

final class NumericSettingNormalizerTests: XCTestCase {
    func testClampsValuesToRange() {
        XCTAssertEqual(
            NumericSettingNormalizer.normalize(-5, in: 1...100, step: 0.5),
            1
        )
        XCTAssertEqual(
            NumericSettingNormalizer.normalize(500, in: 1...100, step: 0.5),
            100
        )
    }

    func testRoundsToStepRelativeToLowerBound() {
        XCTAssertEqual(
            NumericSettingNormalizer.normalize(0.26, in: 0.1...5, step: 0.05),
            0.25,
            accuracy: 0.000_001
        )
        XCTAssertEqual(
            NumericSettingNormalizer.normalize(2.76, in: 0.5...10, step: 0.5),
            3,
            accuracy: 0.000_001
        )
    }

    func testRejectsNonFiniteValues() {
        XCTAssertEqual(
            NumericSettingNormalizer.normalize(.infinity, in: 0.5...10, step: 0.5),
            0.5
        )
        XCTAssertEqual(
            NumericSettingNormalizer.normalize(.nan, in: 1...50, step: 1),
            1
        )
    }

    func testInvalidStepStillClamps() {
        XCTAssertEqual(
            NumericSettingNormalizer.normalize(7.25, in: 1...10, step: 0),
            7.25
        )
    }
}
