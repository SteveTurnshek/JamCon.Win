import XCTest
@testable import JamCon

final class JoyConPacketTimingTests: XCTestCase {
    func testUsesHostReceiptClockAndHandlesTimerWraparound() {
        var tracker = JoyConPacketTimingTracker()

        let first = tracker.observe(
            timerByte: 254,
            bytes: [0x30, 254, 1],
            receivedTimestamp: 10
        )
        let second = tracker.observe(
            timerByte: 1,
            bytes: [0x30, 1, 2],
            receivedTimestamp: 10.016
        )

        XCTAssertTrue(first.accepted)
        XCTAssertEqual(first.timestampSource, .hostReceipt)
        XCTAssertTrue(second.accepted)
        XCTAssertEqual(second.timerDeltaTicks, 3)
        XCTAssertEqual(second.timestampSource, .hostReceipt)
        XCTAssertEqual(second.processingTimestamp, 10.016, accuracy: 0.000_001)
    }

    func testDropsOnlyAnExactRepeatedPacketWithinDuplicateHorizon() {
        var tracker = JoyConPacketTimingTracker()
        let bytes: [UInt8] = [0x30, 10, 1, 2, 3]

        _ = tracker.observe(timerByte: 10, bytes: bytes, receivedTimestamp: 1)
        let duplicate = tracker.observe(
            timerByte: 10,
            bytes: bytes,
            receivedTimestamp: 1.001
        )
        let changed = tracker.observe(
            timerByte: 10,
            bytes: [0x30, 10, 1, 2, 4],
            receivedTimestamp: 1.002
        )

        XCTAssertFalse(duplicate.accepted)
        XCTAssertTrue(duplicate.isDuplicate)
        XCTAssertTrue(changed.accepted)
        XCTAssertTrue(changed.timerDiscontinuity)
        XCTAssertEqual(changed.timestampSource, .hostReceipt)
    }

    func testReanchorsOnAmbiguousTimerJump() {
        var tracker = JoyConPacketTimingTracker()
        _ = tracker.observe(timerByte: 10, bytes: [10], receivedTimestamp: 1)
        let jumped = tracker.observe(timerByte: 200, bytes: [200], receivedTimestamp: 1.01)

        XCTAssertTrue(jumped.accepted)
        XCTAssertTrue(jumped.timerDiscontinuity)
        XCTAssertEqual(jumped.timestampSource, .hostReceipt)
        XCTAssertEqual(jumped.processingTimestamp, 1.01)
    }

    func testTransportSummarySeparatesCallbacksFromAcceptedPackets() throws {
        var aggregator = JoyConTransportAggregator(interval: 1)
        let accepted = JoyConPacketTimingObservation(
            accepted: true,
            processingTimestamp: 1,
            timestampSource: .hostReceipt,
            callbackIntervalMilliseconds: 15,
            acceptedIntervalMilliseconds: 15,
            timerDeltaTicks: 3,
            timerDiscontinuity: false
        )
        let duplicate = JoyConPacketTimingObservation(
            accepted: false,
            processingTimestamp: 1.001,
            timestampSource: .hostReceipt,
            callbackIntervalMilliseconds: 1,
            acceptedIntervalMilliseconds: 1,
            timerDeltaTicks: 0,
            timerDiscontinuity: false
        )

        XCTAssertNil(aggregator.record(accepted, at: 1))
        XCTAssertNil(aggregator.record(duplicate, at: 1.001))
        let summary = try XCTUnwrap(aggregator.record(accepted, at: 2))

        XCTAssertEqual(summary.callbackCount, 3)
        XCTAssertEqual(summary.acceptedCount, 2)
        XCTAssertEqual(summary.duplicateCount, 1)
        XCTAssertEqual(summary.averageTimerDeltaTicks ?? -1, 3)
        XCTAssertTrue(summary.logMessage.contains("duplicates=1"))
    }
}
