#!/usr/bin/env python3
"""Simulated BACnet/IP device with COMMANDABLE objects (Priority_Array / Relinquish_Default) plus ReinitializeDevice,
DeviceCommunicationControl and TimeSynchronization handlers, for testing priority-array editing, Global Commander,
Find Priorities Global and device control of the YABE macOS port. Device 603 on 127.0.0.1:47813.

Usage: .venv/bin/python sim_commandable.py [--address 127.0.0.1:47813] [--instance 603]
Behaviour of the device services:
  ReinitializeDevice : password "secret" (or none) -> SimpleAck (recorded, no real restart); any other password -> Error security/password-failure
  DeviceCommunicationControl : recorded, SimpleAck (communication is NOT really disabled so the simulator stays testable);
                               password "bad" -> Error security/password-failure
  TimeSynchronization / UTCTimeSynchronization : recorded in device description + printed
"""
import asyncio, sys

from bacpypes3.argparse import SimpleArgumentParser
from bacpypes3.app import Application
from bacpypes3.apdu import SimpleAckPDU
from bacpypes3.errors import ExecutionError
from bacpypes3.local.analog import AnalogOutputObject, AnalogValueObjectCmd
from bacpypes3.local.binary import BinaryOutputObject
from bacpypes3.local.multistate import MultiStateOutputObject
from bacpypes3.basetypes import EngineeringUnits

LOG = []


class CmdApp(Application):
    async def do_ReinitializeDeviceRequest(self, apdu):
        LOG.append(("reinit", str(apdu.reinitializedStateOfDevice), apdu.password))
        print("ReinitializeDevice", apdu.reinitializedStateOfDevice, repr(apdu.password), flush=True)
        if apdu.password not in (None, "", "secret"):
            raise ExecutionError(errorClass="security", errorCode="passwordFailure")
        await self.response(SimpleAckPDU(context=apdu))

    async def do_DeviceCommunicationControlRequest(self, apdu):
        LOG.append(("dcc", str(apdu.enableDisable), apdu.timeDuration, apdu.password))
        print("DeviceCommunicationControl", apdu.enableDisable, apdu.timeDuration, repr(apdu.password), flush=True)
        if apdu.password == "bad":
            raise ExecutionError(errorClass="security", errorCode="passwordFailure")
        await self.response(SimpleAckPDU(context=apdu))

    async def do_TimeSynchronizationRequest(self, apdu):
        LOG.append(("timesync", str(apdu.time)))
        print("TimeSynchronization", apdu.time, flush=True)

    async def do_UTCTimeSynchronizationRequest(self, apdu):
        LOG.append(("utctimesync", str(apdu.time)))
        print("UTCTimeSynchronization", apdu.time, flush=True)


def add_objects(app):
    app.add_object(AnalogOutputObject(
        objectIdentifier=("analog-output", 1), objectName="Valve Command", presentValue=0.0,
        relinquishDefault=0.0, covIncrement=0.5, statusFlags=[0, 0, 0, 0], units=EngineeringUnits.percent,
        description="Commandable valve"))
    app.add_object(AnalogOutputObject(
        objectIdentifier=("analog-output", 2), objectName="Damper Command", presentValue=50.0,
        relinquishDefault=50.0, covIncrement=0.5, statusFlags=[0, 0, 0, 0], units=EngineeringUnits.percent))
    app.add_object(AnalogValueObjectCmd(
        objectIdentifier=("analog-value", 1), objectName="Cmd Setpoint", presentValue=21.0,
        relinquishDefault=21.0, covIncrement=0.1, statusFlags=[0, 0, 0, 0], units=EngineeringUnits.degreesCelsius))
    app.add_object(BinaryOutputObject(
        objectIdentifier=("binary-output", 1), objectName="Fan Relay", presentValue="inactive",
        relinquishDefault="inactive", statusFlags=[0, 0, 0, 0], polarity="normal"))
    app.add_object(MultiStateOutputObject(
        objectIdentifier=("multi-state-output", 1), objectName="Mode Command", presentValue=1,
        relinquishDefault=1, numberOfStates=3, stateText=["Off", "Heat", "Cool"], statusFlags=[0, 0, 0, 0]))


async def main():
    parser = SimpleArgumentParser()
    args = parser.parse_args(["--name", "YABE Commandable Simulator", "--instance", "603",
                              "--address", "127.0.0.1:47813"] + sys.argv[1:])
    app = CmdApp.from_args(args)
    add_objects(app)
    print(f"Simulator device {args.instance} listening on {args.address}", flush=True)
    await asyncio.Event().wait()


if __name__ == "__main__":
    asyncio.run(main())
