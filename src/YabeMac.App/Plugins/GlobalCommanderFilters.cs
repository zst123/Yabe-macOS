using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Linq;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    // Filters and "Fetch Descriptions" of the GlobalCommander window (second half of GlobalCommander.cs).
    public partial class GlobalCommanderWindow
    {
        // ------------------------------------------------------------------ Fetch descriptions
        async void FetchDescriptionsDevice()
        {
            StartPatienceTimer();
            progBar.Value = 0;
            var items = deviceItems.ToList();
            cmdFetchDescriptionsDevice.IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        SetProgress(100 * i / items.Count);
                        var item = items[i];
                        if (!(item.Tag is GcDevice dev)) continue;
                        string description = null;
                        try
                        {
                            bool got = dev.Device.ReadPropertyRequest(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, dev.Device.deviceId), BacnetPropertyIds.PROP_DESCRIPTION, out IList<BacnetValue> list);
                            if (got && list != null && list.Count > 0) description = list[0].Value.ToString();
                        }
                        catch { }
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            string d = description;
                            Dispatcher.UIThread.Post(() => { item.Text = item.Name + " - " + d; });
                        }
                    }
                });
            }
            finally { cmdFetchDescriptionsDevice.IsEnabled = true; }
            progBar.Value = 100;
            ResetPatience();
        }

        async void FetchDescriptionsPoint()
        {
            StartPatienceTimer();
            progBar.Value = 10;
            var items = pointItems.ToList();
            int successes = 0; string lastError = string.Empty; bool unknown = false;
            cmdFetchDescriptionsPoint.IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        int pointListProgress = 0;
                        int estimatedStringLength = 10;
                        while (pointListProgress < items.Count)
                        {
                            var deferredReads = new Dictionary<GcDevice, IList<BacnetReadAccessSpecification>>();
                            var track = new Dictionary<GcDevice, Dictionary<BacnetObjectId, Tuple<GcPoint, GcItem>>>();
                            for (int i = pointListProgress; i < items.Count; i++)
                            {
                                var item = items[i];
                                if (!(item.Tag is GcPoint point) || point.ParentDevice == null) continue;
                                var dev = point.ParentDevice;
                                if (!track.ContainsKey(dev))
                                {
                                    deferredReads.Add(dev, new List<BacnetReadAccessSpecification>());
                                    track.Add(dev, new Dictionary<BacnetObjectId, Tuple<GcPoint, GcItem>>());
                                }
                                if (!track[dev].ContainsKey(point.ObjectID))
                                {
                                    deferredReads[dev].Add(new BacnetReadAccessSpecification(point.ObjectID, new List<BacnetPropertyReference> { new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_DESCRIPTION, ASN1.BACNET_ARRAY_ALL) }));
                                    track[dev].Add(point.ObjectID, new Tuple<GcPoint, GcItem>(point, item));
                                }
                                if ((i + 1 - pointListProgress) > ((Math.Min(dev.Device.MaxAPDULenght, (uint)dev.Device.channel.GetMaxApdu()) - 10) / (estimatedStringLength + 9)))
                                { pointListProgress = i + 1; break; }
                                else if ((i + 1) >= items.Count) pointListProgress = items.Count;
                            }

                            foreach (var deviceReads in deferredReads)
                            {
                                var dev = deviceReads.Key;
                                var reads = deviceReads.Value;
                                var resultItems = track[dev];
                                int storeTimeout = dev.Device.channel.Timeout;
                                int factor = 4;
                                try
                                {
                                    dev.Device.channel.Timeout = Math.Min(dev.Device.channel.Timeout, int.MaxValue / factor) * factor;
                                    if (dev.Device.ReadPropertyMultipleRequest(reads, out IList<BacnetReadAccessResult> propsReadList))
                                    {
                                        for (int i = 0; i < propsReadList.Count; i++)
                                        {
                                            BacnetObjectId object_id = propsReadList[i].objectIdentifier;
                                            var point = resultItems[object_id].Item1;
                                            var item = resultItems[object_id].Item2;
                                            for (int j = 0; j < propsReadList[i].values.Count; j++)
                                            {
                                                IList<BacnetValue> values = propsReadList[i].values[j].value;
                                                if (propsReadList[i].values[j].property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_DESCRIPTION && values != null && values.Count > 0 && values[0].Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR)
                                                {
                                                    point.Description = values[0].ToString();
                                                    if (!string.IsNullOrWhiteSpace(point.Description))
                                                    {
                                                        string txt = item.Name + " - " + point.Description;
                                                        Dispatcher.UIThread.Post(() => item.Text = txt);
                                                    }
                                                    successes++;
                                                }
                                            }
                                        }
                                    }
                                    else lastError = "Timeout";
                                }
                                catch (Exception ex) { lastError = ex.Message; }
                                finally
                                {
                                    dev.Device.channel.Timeout = storeTimeout;
                                    SetProgress(10 + 90 * pointListProgress / items.Count);
                                }
                            }
                        }
                    }
                    catch { unknown = true; }
                });
            }
            finally { cmdFetchDescriptionsPoint.IsEnabled = true; }

            if (unknown) { await Msg("Unknown error retrieving descriptors."); progBar.Value = 0; }
            else if (items.Count != successes)
            {
                if (successes == 0) { await Msg("Failed to retrieve any descriptors." + Environment.NewLine + "Last error: " + lastError); progBar.Value = 0; }
                // else: some errors occurred, retrying may help
            }
            else progBar.Value = 100;
            ResetPatience();
        }

        // ------------------------------------------------------------------ Device / point / property filters
        void DeviceFilterChanged()
        {
            if (!txtDeviceFilter.IsEnabled) return;
            ResetFromObjectsList();
            _selectedDevices = null;
            _suppress++;
            try
            {
                DeviceList.SelectedItems?.Clear();
                deviceItems.Clear();
                if (_commonDevices != null && _commonDevices.Count > 0)
                    foreach (var d in FilterCommonDevices(txtDeviceFilter.Text)) deviceItems.Add(DeviceItem(d));
            }
            finally { _suppress--; }
        }

        List<GcDevice> FilterCommonDevices(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return _commonDevices;
            return _commonDevices.Where(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        void PointFilterChanged(TextBox tb)
        {
            ObjectTextFilterTimer.Stop();
            if (!tb.IsEnabled) return;
            ObjectTextFilterTimer.Start();
        }

        async void ObjectTextFilterTick()
        {
            ObjectTextFilterTimer.Stop();
            if (!txtPointFilter.IsEnabled || !txtPointFilter2.IsEnabled) return;
            try { _pointListTextFiltered = FilterCommonPoints(txtPointFilter.Text, txtPointFilter2.Text); }
            catch (Exception ex)
            {
                _pointListTextFiltered = null;
                await Msg("Error in text filter: " + ex.Message);
            }
            finally { UpdateObjectListWithFilter(); }
        }

        List<GcPoint> FilterCommonPoints(string f1, string f2)
        {
            if (string.IsNullOrEmpty(f1) && string.IsNullOrEmpty(f2)) return null;
            if (_pointListForDisplay == null) return null;
            var filtered = new List<GcPoint>();
            foreach (var point in _pointListForDisplay)
                if ((string.IsNullOrEmpty(f1) || point.SearchString.Contains(f1, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(f2) || point.SearchString.Contains(f2, StringComparison.OrdinalIgnoreCase)))
                    filtered.Add(point);
            return filtered;
        }

        void UpdateObjectListWithFilter()
        {
            ResetFromPropertyList();
            _selectedPoints = null; _allSelectedPoints = null;
            IEnumerable<GcPoint> union;
            if (_pointListTextFiltered != null)
                union = _pointListAdvancedFiltered != null ? _pointListAdvancedFiltered.Intersect(_pointListTextFiltered) : _pointListTextFiltered;
            else
                union = _pointListAdvancedFiltered != null ? _pointListAdvancedFiltered : _pointListForDisplay;
            FillPoints(union);
        }

        async void AdvancedFilterClick()
        {
            var advFilt = new GcAdvancedFilterWindow(Enum.GetValues(typeof(FilterAdvancedOperation)).Cast<object>());
            cmdAdvancedFilter.IsEnabled = false;
            bool cancelled = false;
            try
            {
                await advFilt.ShowDialog(this);
                switch (advFilt.Outcome)
                {
                    case GcAdvancedFilterWindow.Result.Ok: break;
                    case GcAdvancedFilterWindow.Result.Reset:
                        _pointListAdvancedFiltered = null;
                        cmdAdvancedFilter.ClearValue(Button.BackgroundProperty);
                        progBar.Value = 100;
                        UpdateObjectListWithFilter();
                        return;
                    default:
                        cancelled = true;
                        return;
                }

                StartPatienceTimer();
                progBar.Value = 0;
                string idStr = advFilt.PropIdText.Text ?? "";
                var operation = (FilterAdvancedOperation)advFilt.OperationCombo.SelectedItem;
                string valStr = advFilt.ValueText.Text ?? "";
                if (!uint.TryParse(idStr, out uint id))
                {
                    await Msg("Invalid property ID.");
                    progBar.Value = 0;
                    return;
                }
                progBar.Value = 10;
                var points = _pointListForDisplay;
                _pointListAdvancedFiltered = await Task.Run(() => FilterAdvanced(points, id, valStr, operation, Progress));
                cmdAdvancedFilter.Background = Brushes.LightBlue;
                progBar.Value = 100;
            }
            catch (Exception ex)
            {
                _pointListAdvancedFiltered = null;
                cmdAdvancedFilter.ClearValue(Button.BackgroundProperty);
                await Msg("Error in advanced filter: " + ex.Message);
                progBar.Value = 0;
            }
            finally
            {
                if (!cancelled && advFilt.Outcome != GcAdvancedFilterWindow.Result.Reset) UpdateObjectListWithFilter();
                cmdAdvancedFilter.IsEnabled = true;
                ResetPatience();
            }
        }

        List<GcPoint> FilterAdvanced(List<GcPoint> pointListForDisplay, uint id, string filterValue, FilterAdvancedOperation operation, Action<int> prog)
        {
            var filteredList = new List<GcPoint>();
            if (filterValue == null) throw new ArgumentNullException("Filter value is invalid.");

            int pointsChecked = 0;
            foreach (var point in pointListForDisplay)
            {
                prog(10 + 90 * pointsChecked++ / pointListForDisplay.Count);
                BACnetDevice device = point.ParentDevice.Device;
                // A read error other than "property does not exist" aborts the whole operation (TryReadProperty throws).
                bool hasProperty = TryReadProperty(point, (BacnetPropertyIds)id, out IList<BacnetValue> value_list);

                if (operation == FilterAdvancedOperation.Has_Property || operation == FilterAdvancedOperation.Does_Not_Have_Property)
                {
                    if (operation == FilterAdvancedOperation.Has_Property ^ !hasProperty) filteredList.Add(point);
                    continue;
                }
                if (!hasProperty) continue;

                var property = new GcProperty(point, (BacnetPropertyIds)id);
                property.Name = device.GetNiceName((BacnetPropertyIds)id, true);
                if (value_list != null)
                {
                    var b_values = value_list.ToList();
                    property.Values.AddRange(b_values);
                    for (int i = 0; i < b_values.Count; i++)
                        property.ValueTypes.Add(b_values[i].Value != null ? b_values[i].Value.GetType() : null);
                }

                switch (operation)
                {
                    case FilterAdvancedOperation.Equals:
                    case FilterAdvancedOperation.Does_not_equal:
                        {
                            bool match = property.Values.Count == 0 ? (filterValue.Length == 0) : property.ValueObjectString.Equals(filterValue);
                            if ((match && operation == FilterAdvancedOperation.Equals) || (!match && operation == FilterAdvancedOperation.Does_not_equal))
                                filteredList.Add(point);
                        }
                        break;
                    case FilterAdvancedOperation.Contains:
                        {
                            string entered = filterValue;
                            if (string.IsNullOrEmpty(entered)) throw new ApplicationException("Invalid filter value specified. Cannot use a blank filter value for \"Contains\".");
                            var pv = property.Values;
                            bool match = false;
                            if (pv != null && pv.Count == 1)
                                match = GcProperty.GetValueObjectString(pv, 0).Contains(entered);
                            else if (pv != null && pv.Count > 1)
                                for (int i = 0; i < pv.Count; i++)
                                    if (GcProperty.GetValueObjectString(pv, i).Equals(entered)) { match = true; break; }
                            if (match) filteredList.Add(point);
                        }
                        break;
                    default:
                        throw new ApplicationException("Invalid filter operation");
                }
            }
            return filteredList;
        }

        static bool TryReadProperty(GcPoint point, BacnetPropertyIds id, out IList<BacnetValue> value_list)
        {
            BACnetDevice device = point.ParentDevice.Device;
            value_list = null;
            try
            {
                if (!device.channel.ReadPropertyRequest(device.BacAdr, point.ObjectID, id, out value_list))
                    throw new ApplicationException(string.Format("Reading property {0} for point {1} failed: Timeout", id.ToString(), point.ObjectID.ToString()));
                return true;
            }
            catch (Exception ex)
            {
                if (ex.Data.Contains("BacnetRejectReasons"))
                    throw new ApplicationException(string.Format("Reading property {0} for point {1} failed: {2}", id.ToString(), point.ObjectID.ToString(), ex.Data["BacnetRejectReasons"].ToString()));
                else if (ex.Data.Contains("BacnetErrorClasses") && ex.Data.Contains("BacnetErrorCodes"))
                {
                    var errorClass = (BacnetErrorClasses)ex.Data["BacnetErrorClasses"];
                    var errorCode = (BacnetErrorCodes)ex.Data["BacnetErrorCodes"];
                    switch (errorClass)
                    {
                        case BacnetErrorClasses.ERROR_CLASS_DEVICE:
                        case BacnetErrorClasses.ERROR_CLASS_OBJECT:
                        case BacnetErrorClasses.ERROR_CLASS_PROPERTY:
                            switch (errorCode)
                            {
                                case BacnetErrorCodes.ERROR_CODE_OTHER:
                                case BacnetErrorCodes.ERROR_CODE_READ_ACCESS_DENIED:
                                case BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT:
                                case BacnetErrorCodes.ERROR_CODE_UNSUPPORTED_OBJECT_TYPE:
                                case BacnetErrorCodes.ERROR_CODE_DATATYPE_NOT_SUPPORTED:
                                case BacnetErrorCodes.ERROR_CODE_INVALID_ARRAY_INDEX:
                                case BacnetErrorCodes.ERROR_CODE_INVALID_DATA_TYPE:
                                case BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY:
                                case BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE:
                                    return false;
                            }
                            break;
                    }
                    throw new ApplicationException(string.Format("Reading property {0} for point {1} failed: {2} - {3}", id.ToString(), point.ObjectID.ToString(), errorClass, errorCode));
                }
                else
                    throw new ApplicationException(string.Format("Reading property {0} for point {1} failed: {2}", id.ToString(), point.ObjectID.ToString(), ex.Message));
            }
        }

        void PropFilterChanged()
        {
            if (!txtPropFilter.IsEnabled) return;
            bool onlyCommon = oComProp.IsChecked == true;
            ResetAfterPropertyList();
            if (_commonProperties != null && _commonProperties.Count > 0)
            {
                var filtered = string.IsNullOrWhiteSpace(txtPropFilter.Text) ? _commonProperties
                    : _commonProperties.Where(p => p.Name.Contains(txtPropFilter.Text, StringComparison.OrdinalIgnoreCase)).ToList();
                _suppress++;
                try { foreach (var p in filtered) propertyItems.Add(PropertyItem(p, onlyCommon)); }
                finally { _suppress--; }
            }
        }
    }
}
