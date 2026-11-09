#!/usr/bin/env python3
"""Simulated BACnet/IP device for testing the YABE macOS port.

Usage: .venv/bin/python sim_server.py [--address 127.0.0.1:47809] [--instance 599]
Point YABE at it with a unicast Who-Is (127.0.0.1:47809).
"""
import asyncio, math, random

from bacpypes3.argparse import SimpleArgumentParser
from bacpypes3.app import Application
from bacpypes3.local.analog import AnalogInputObject, AnalogValueObject
from bacpypes3.local.binary import BinaryValueObject
from bacpypes3.local.multistate import MultiStateValueObject
from bacpypes3.basetypes import EngineeringUnits


def add_objects(app):
    app.add_object(AnalogInputObject(
        objectIdentifier=("analog-input", 1), objectName="Room Temperature",
        presentValue=21.5, covIncrement=0.1, statusFlags=[0, 0, 0, 0], units=EngineeringUnits.degreesCelsius,
        description="Drifting temperature"))
    app.add_object(AnalogInputObject(
        objectIdentifier=("analog-input", 2), objectName="Humidity",
        presentValue=45.0, covIncrement=0.5, statusFlags=[0, 0, 0, 0], units=EngineeringUnits.percentRelativeHumidity))
    app.add_object(AnalogValueObject(
        objectIdentifier=("analog-value", 1), objectName="Setpoint",
        presentValue=22.0, covIncrement=0.1, statusFlags=[0, 0, 0, 0], units=EngineeringUnits.degreesCelsius,
        description="Writable setpoint"))
    app.add_object(BinaryValueObject(
        objectIdentifier=("binary-value", 1), objectName="Fan Enable",
        presentValue="inactive", statusFlags=[0, 0, 0, 0]))
    app.add_object(MultiStateValueObject(
        objectIdentifier=("multi-state-value", 1), objectName="Mode",
        presentValue=1, numberOfStates=3, stateText=["Off", "Heat", "Cool"],
        statusFlags=[0, 0, 0, 0]))


async def drift(app):
    t = 0.0
    while True:
        await asyncio.sleep(2)
        t += 0.2
        app.objectName["Room Temperature"].presentValue = round(21.5 + 2 * math.sin(t) + random.uniform(-0.1, 0.1), 2)
        app.objectName["Humidity"].presentValue = round(45 + 5 * math.cos(t / 2), 1)


async def main():
    parser = SimpleArgumentParser()
    args = parser.parse_args(["--name", "YABE Simulator", "--instance", "599",
                              "--address", "127.0.0.1:47809"] + __import__("sys").argv[1:])
    app = Application.from_args(args)
    add_objects(app)
    print(f"Simulator device {args.instance} listening on {args.address}", flush=True)
    await drift(app)


if __name__ == "__main__":
    asyncio.run(main())
