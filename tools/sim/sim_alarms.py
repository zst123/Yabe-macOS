#!/usr/bin/env python3
"""Simulated BACnet/IP device with alarm objects, for testing the YABE macOS port (Alarms area).

Usage: .venv/bin/python sim_alarms.py [--address 127.0.0.1:47811] [--instance 601] [--notify 127.0.0.1:47808]
 * notification-class,1 with a recipient list and priorities (Notification Editor)
 * analog-input,1 (OFFNORMAL) / analog-input,2 (FAULT) with unacknowledged transitions (Alarm summary, Ack buttons)
 * GetEventInformation and AcknowledgeAlarm services
 * with --notify host:port: sends an UnconfirmedEventNotification to that address every 10 s
"""
import asyncio, sys, time

from bacpypes3.argparse import SimpleArgumentParser
from bacpypes3.app import Application
from bacpypes3.local.analog import AnalogInputObject
from bacpypes3.object import NotificationClassObject
from bacpypes3.basetypes import (EngineeringUnits, EventTransitionBits, TimeStamp, EventState,
                                 GetEventInformationEventSummary, NotifyType, EventType, DateTime)
from bacpypes3.primitivedata import Time, Date, ObjectIdentifier
from bacpypes3.constructeddata import ArrayOf
from bacpypes3.pdu import Address
from bacpypes3.apdu import (GetEventInformationRequest, GetEventInformationACK, AcknowledgeAlarmRequest,
                            SimpleAckPDU, UnconfirmedEventNotificationRequest)
from bacpypes3.errors import ExecutionError

def ts_now():
    return TimeStamp(dateTime=DateTime(date=Date.now(), time=Time.now()))  # the original decoder wants dateTime


class AlarmApp(Application):
    async def do_GetEventInformationRequest(self, apdu: GetEventInformationRequest):
        summaries = []
        for obj in self.objectIdentifier.values():
            if getattr(obj, "eventState", None) is None:
                continue
            acked = obj.ackedTransitions
            if str(obj.eventState) == "normal" and all(acked):
                continue
            summaries.append(GetEventInformationEventSummary(
                objectIdentifier=obj.objectIdentifier, eventState=obj.eventState,
                acknowledgedTransitions=acked, eventTimeStamps=obj.eventTimeStamps,
                notifyType="alarm", eventEnable=EventTransitionBits([1, 1, 1]), eventPriorities=[3, 3, 3]))
        await self.response(GetEventInformationACK(listOfEventSummaries=summaries, moreEvents=False, context=apdu))

    async def do_AcknowledgeAlarmRequest(self, apdu: AcknowledgeAlarmRequest):
        obj = self.get_object_id(apdu.eventObjectIdentifier)
        if not obj:
            raise ExecutionError(errorClass="object", errorCode="unknownObject")
        state = str(apdu.eventStateAcknowledged)
        bit = {"offnormal": 0, "fault": 1, "normal": 2}.get(state)
        if bit is None:
            raise ExecutionError(errorClass="services", errorCode="invalidEventState")
        acked = list(obj.ackedTransitions)
        acked[bit] = 1
        obj.ackedTransitions = EventTransitionBits(acked)
        print(f"ACK {apdu.eventObjectIdentifier} {state} by '{apdu.acknowledgmentSource}'", flush=True)
        await self.response(SimpleAckPDU(context=apdu))


def add_objects(app):
    app.add_object(NotificationClassObject(
        objectIdentifier=("notification-class", 1), objectName="Notif Class 1", notificationClass=1,
        priority=[3, 4, 5], ackRequired=EventTransitionBits([1, 1, 0]),
        recipientList=[{
            "validDays": [1, 1, 1, 1, 1, 1, 1],
            "fromTime": "00:00:00.00", "toTime": "23:59:59.99",
            "recipient": {"device": ("device", 1234)},
            "processIdentifier": 7, "issueConfirmedNotifications": False,
            "transitions": [1, 1, 1]}]))
    for inst, state, acked, name in ((1, "offnormal", [0, 1, 1], "Alarm Temp"), (2, "fault", [1, 0, 1], "Fault Sensor")):
        app.add_object(AnalogInputObject(
            objectIdentifier=("analog-input", inst), objectName=name, presentValue=99.0,
            statusFlags=[1, 0, 0, 0], units=EngineeringUnits.degreesCelsius, description="alarm test",
            eventState=state, ackedTransitions=EventTransitionBits(acked),
            eventTimeStamps=[ts_now(), ts_now(), ts_now()], notificationClass=1))


async def notify_loop(app, target):
    n = 0
    while True:
        await asyncio.sleep(10)
        n += 1
        try:
            req = UnconfirmedEventNotificationRequest(
                destination=Address(target), processIdentifier=n,
                initiatingDeviceIdentifier=app.device_object.objectIdentifier,
                eventObjectIdentifier=("analog-input", 1), timeStamp=ts_now(), notificationClass=1, priority=3,
                eventType="outOfRange", notifyType="alarm", ackRequired=False,
                fromState="normal", toState="offnormal", messageText="sim event %d" % n)
            await app.request(req)
            print("sent event notification", n, flush=True)
        except Exception as e:
            print("notify failed:", repr(e), flush=True)


async def main():
    argv = sys.argv[1:]
    notify = None
    if "--notify" in argv:
        i = argv.index("--notify"); notify = argv[i + 1]; del argv[i:i + 2]
    parser = SimpleArgumentParser()
    args = parser.parse_args(["--name", "YABE Alarm Simulator", "--instance", "601",
                              "--address", "127.0.0.1:47811"] + argv)
    app = AlarmApp.from_args(args)
    add_objects(app)
    print(f"Alarm simulator device {args.instance} listening on {args.address}", flush=True)
    if notify:
        asyncio.ensure_future(notify_loop(app, notify))
    await asyncio.Event().wait()


if __name__ == "__main__":
    asyncio.run(main())
