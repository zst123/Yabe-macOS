#!/usr/bin/env python3
"""Simulated BACnet/IP device with a Trend Log (ReadRange by position) for testing the TrendLogDisplay of the YABE macOS
port. Device 604 on 127.0.0.1:47814. Usage: .venv/bin/python sim_trendlog.py
"""
import asyncio, math, sys, datetime

from bacpypes3.argparse import SimpleArgumentParser
from bacpypes3.app import Application
from bacpypes3.local.object import Object as _Object
from bacpypes3.object import TrendLogObject as _TL
from bacpypes3.apdu import ReadRangeACK, ReadRangeRequest
from bacpypes3.basetypes import DateTime, LogRecord, LogRecordLogDatum, ResultFlags
from bacpypes3.primitivedata import Date, Time, Real
from bacpypes3.constructeddata import Any, SequenceOf


class TrendLogObject(_Object, _TL): pass


def make_records(n=120):
    t0 = datetime.datetime.now().replace(microsecond=0) - datetime.timedelta(minutes=n)
    recs = []
    for i in range(n):
        t = t0 + datetime.timedelta(minutes=i)
        recs.append(LogRecord(
            timestamp=DateTime(date=Date((t.year, t.month, t.day, t.isoweekday())),
                               time=Time((t.hour, t.minute, t.second, 0))),
            logDatum=LogRecordLogDatum(realValue=Real(20.0 + 5 * math.sin(i / 10.0))),
        ))
    return recs


class TrendApp(Application):
    records = []

    async def do_ReadRangeRequest(self, apdu):
        pos = getattr(apdu.range, "byPosition", None) if apdu.range is not None else None
        first, count = (pos.referenceIndex, pos.count) if pos is not None else (1, len(self.records))
        sel = self.records[first - 1:first - 1 + abs(count)]
        ack = ReadRangeACK(
            objectIdentifier=apdu.objectIdentifier, propertyIdentifier=apdu.propertyIdentifier,
            resultFlags=ResultFlags([first == 1, first - 1 + len(sel) >= len(self.records), first - 1 + len(sel) < len(self.records)]),
            itemCount=len(sel), itemData=SequenceOf(LogRecord)(sel), firstSequenceNumber=first,
            context=apdu,
        )
        await self.response(ack)


async def main():
    parser = SimpleArgumentParser()
    args = parser.parse_args(["--name", "YABE TrendLog Simulator", "--instance", "604",
                              "--address", "127.0.0.1:47814"] + sys.argv[1:])
    app = TrendApp.from_args(args)
    recs = make_records()
    TrendApp.records = recs
    app.add_object(TrendLogObject(objectIdentifier=("trend-log", 1), objectName="Room Temp Log", enable=True,
                                  stopWhenFull=False, bufferSize=1000, recordCount=len(recs), totalRecordCount=len(recs),
                                  loggingType="polled", statusFlags=[0, 0, 0, 0], logInterval=6000))
    print(f"Simulator device {args.instance} listening on {args.address}", flush=True)
    await asyncio.Event().wait()


if __name__ == "__main__":
    asyncio.run(main())
