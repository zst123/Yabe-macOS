#!/usr/bin/env python3
"""Simulated BACnet/IP device with writable Date-Value, Time-Value and DateTime-Value objects, for testing the date/time
editors of the YABE macOS port. Device 602 on 127.0.0.1:47812.

Usage: .venv/bin/python sim_datetime.py [--address 127.0.0.1:47812] [--instance 602]
"""
import asyncio, sys

from bacpypes3.argparse import SimpleArgumentParser
from bacpypes3.app import Application
from bacpypes3.local.object import Object as _Object
from bacpypes3.object import DateValueObject as _DV, TimeValueObject as _TV, DateTimeValueObject as _DTV
from bacpypes3.primitivedata import Date, Time
from bacpypes3.basetypes import DateTime


class DateValueObject(_Object, _DV): pass
class TimeValueObject(_Object, _TV): pass
class DateTimeValueObject(_Object, _DTV): pass


def add_objects(app):
    app.add_object(DateValueObject(objectIdentifier=("date-value", 1), objectName="Holiday Date",
                                   presentValue=Date("2026-12-25"), statusFlags=[0, 0, 0, 0]))
    app.add_object(TimeValueObject(objectIdentifier=("time-value", 1), objectName="Start Time",
                                   presentValue=Time("07:30:00.00"), statusFlags=[0, 0, 0, 0]))
    app.add_object(DateTimeValueObject(objectIdentifier=("datetime-value", 1), objectName="Last Maintenance",
                                       presentValue=DateTime(date=Date("2026-03-04"), time=Time("12:15:30.00")),
                                       statusFlags=[0, 0, 0, 0]))


async def main():
    parser = SimpleArgumentParser()
    args = parser.parse_args(["--name", "YABE DateTime Simulator", "--instance", "602",
                              "--address", "127.0.0.1:47812"] + sys.argv[1:])
    app = Application.from_args(args)
    add_objects(app)
    print(f"Simulator device {args.instance} listening on {args.address}", flush=True)
    await asyncio.Event().wait()


if __name__ == "__main__":
    asyncio.run(main())
