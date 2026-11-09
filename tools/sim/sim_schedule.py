#!/usr/bin/env python3
"""Simulated BACnet/IP device with a Schedule and a Calendar object, for testing the YABE macOS port
(Schedule editor / Calendar editor). Device 600 on 127.0.0.1:47810 (the shared sim stays on 47809).

Usage: .venv/bin/python sim_schedule.py [--address 127.0.0.1:47810] [--instance 600]
Point YABE at it with a unicast Who-Is (127.0.0.1:47810).
"""
import asyncio, sys

from bacpypes3.argparse import SimpleArgumentParser
from bacpypes3.app import Application
from bacpypes3.local.analog import AnalogValueObject
from bacpypes3.object import ScheduleObject, CalendarObject
from bacpypes3.basetypes import (EngineeringUnits, DailySchedule, TimeValue, DateRange, CalendarEntry,
                                 SpecialEvent, SpecialEventPeriod, DeviceObjectPropertyReference,
                                 StatusFlags, WeekNDay, Reliability)
from bacpypes3.primitivedata import Date, Time, Real, Null, ObjectIdentifier, Unsigned
from bacpypes3.constructeddata import AnyAtomic, ArrayOf, SequenceOf
from bacpypes3.basetypes import DailySchedule as _DS


def tv(t, v):
    return TimeValue(time=Time(t), value=AnyAtomic(Real(v)))


def add_objects(app):
    app.add_object(AnalogValueObject(
        objectIdentifier=("analog-value", 1), objectName="Setpoint",
        presentValue=22.0, statusFlags=[0, 0, 0, 0], units=EngineeringUnits.degreesCelsius))

    weekly = [DailySchedule(daySchedule=[tv("07:00:00", 21.0), tv("18:00:00", 17.0)]) for _ in range(5)]
    weekly += [DailySchedule(daySchedule=[]), DailySchedule(daySchedule=[])]
    exception = [
        SpecialEvent(
            period=SpecialEventPeriod(calendarEntry=CalendarEntry(date=Date("2026-12-25"))),
            listOfTimeValues=[tv("00:00:00", 15.0)], eventPriority=16),
        SpecialEvent(
            period=SpecialEventPeriod(calendarReference=ObjectIdentifier("calendar,1")),
            listOfTimeValues=[tv("00:00:00", 12.0)], eventPriority=10),
    ]
    sched = ScheduleObject(
        objectIdentifier=("schedule", 1), objectName="Heating Schedule",
        presentValue=AnyAtomic(Real(17.0)), scheduleDefault=AnyAtomic(Real(15.0)),
        effectivePeriod=DateRange(startDate=Date("2026-01-01"), endDate=Date("2026-12-31")),
        weeklySchedule=weekly, exceptionSchedule=exception,
        listOfObjectPropertyReferences=[DeviceObjectPropertyReference(
            objectIdentifier=("analog-value", 1), propertyIdentifier="present-value")],
        priorityForWriting=16, statusFlags=[0, 0, 0, 0], reliability=Reliability.noFaultDetected, outOfService=False)
    app.add_object(sched)

    cal = CalendarObject(
        objectIdentifier=("calendar", 1), objectName="Holidays", presentValue=False,
        dateList=[
            CalendarEntry(date=Date("2026-12-25")),
            CalendarEntry(dateRange=DateRange(startDate=Date("2026-07-01"), endDate=Date("2026-07-14"))),
            CalendarEntry(weekNDay=WeekNDay(bytes([255, 1, 1]))),   # every Monday, any month, days 1-7
        ])
    app.add_object(cal)


async def main():
    parser = SimpleArgumentParser()
    args = parser.parse_args(["--name", "YABE Schedule Simulator", "--instance", "600",
                              "--address", "127.0.0.1:47810"] + sys.argv[1:])
    app = Application.from_args(args)
    add_objects(app)
    print(f"Schedule simulator device {args.instance} listening on {args.address}", flush=True)
    await asyncio.Event().wait()


if __name__ == "__main__":
    asyncio.run(main())
