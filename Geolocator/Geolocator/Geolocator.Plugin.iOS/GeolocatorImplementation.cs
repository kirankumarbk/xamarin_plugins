
//
//  Copyright 2011-2013, Xamarin Inc.
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//

using System;

using System.Threading.Tasks;
using System.Threading;

#if __UNIFIED__
using CoreLocation;
using Foundation;
using UIKit;

#else
using MonoTouch.CoreLocation;
using MonoTouch.Foundation;
using MonoTouch.UIKit;
#endif
using Plugin.Geolocator.Abstractions;


namespace Plugin.Geolocator
{
    /// <summary>
    /// Implementation for Geolocator
    /// </summary>
    public class GeolocatorImplementation : IGeolocator
    {
        bool deferringUpdates;

        public GeolocatorImplementation()
        {
            DesiredAccuracy = 100;
            manager = GetManager();
            manager.AuthorizationChanged += OnAuthorizationChanged;
            manager.Failed += OnFailed;

            if (UIDevice.CurrentDevice.CheckSystemVersion(6, 0))
                manager.LocationsUpdated += OnLocationsUpdated;
            else
                manager.UpdatedLocation += OnUpdatedLocation;

            manager.UpdatedHeading += OnUpdatedHeading;

            manager.DeferredUpdatesFinished += OnDeferredUpdatedFinished;

            RequestAuthorization();
        }

        void OnDeferredUpdatedFinished (object sender, NSErrorEventArgs e)
        {
            deferringUpdates = false;
        }

        void RequestAuthorization()
        {
            var info = NSBundle.MainBundle.InfoDictionary;

            if (UIDevice.CurrentDevice.CheckSystemVersion(8, 0))
            {
                if (info.ContainsKey(new NSString("NSLocationWhenInUseUsageDescription")))
                    manager.RequestWhenInUseAuthorization();
                else if (info.ContainsKey(new NSString("NSLocationAlwaysUsageDescription")))
                    manager.RequestAlwaysAuthorization();
                else
                    throw new UnauthorizedAccessException("On iOS 8.0 and higher you must set either NSLocationWhenInUseUsageDescription or NSLocationAlwaysUsageDescription in your Info.plist file to enable Authorization Requests for Location updates!");
            }
        }

        /// <inheritdoc/>
        public event EventHandler<PositionErrorEventArgs> PositionError;
        /// <inheritdoc/>
        public event EventHandler<PositionEventArgs> PositionChanged;

        /// <inheritdoc/>
        public double DesiredAccuracy
        {
            get;
            set;
        }

        /// <inheritdoc/>
        public bool IsListening
        {
            get { return isListening; }
        }

        /// <inheritdoc/>
        public bool SupportsHeading
        {
            get { return CLLocationManager.HeadingAvailable; }
        }

        bool pausesLocationUpdatesAutomatically;

        /// <inheritdoc/>
        public bool PausesLocationUpdatesAutomatically
        {
            get
            {
                return pausesLocationUpdatesAutomatically;
            }
            set
            {
                pausesLocationUpdatesAutomatically = value;
                if (UIDevice.CurrentDevice.CheckSystemVersion(6, 0))
                {
                    if (manager != null)
                        manager.PausesLocationUpdatesAutomatically = value;
                }
            }
        }

        EnergySettings energySettings;

        bool allowsBackgroundUpdates;

        /// <inheritdoc/>
        public bool AllowsBackgroundUpdates
        {
            get
            {
                return allowsBackgroundUpdates;
            }
            set
            {
                allowsBackgroundUpdates = value;
                if (UIDevice.CurrentDevice.CheckSystemVersion(9, 0))
                {
                    if (manager != null)
                        manager.AllowsBackgroundLocationUpdates = value;
                }
            }
        }

        /// <inheritdoc/>
        public bool IsGeolocationAvailable
        {
            get { return true; } // all iOS devices support at least wifi geolocation
        }

        /// <inheritdoc/>
        public bool IsGeolocationEnabled
        {
            get
            {
                var status = CLLocationManager.Status;

                if (UIDevice.CurrentDevice.CheckSystemVersion(8, 0))
                {
                    return status == CLAuthorizationStatus.AuthorizedAlways
                    || status == CLAuthorizationStatus.AuthorizedWhenInUse;
                }
               
                return status == CLAuthorizationStatus.Authorized;
            }
        }


        /// <inheritdoc/>
        public Task<Position> GetPositionAsync(int timeoutMilliseconds = Timeout.Infinite, CancellationToken? cancelToken = null, bool includeHeading = false)
        {
            if (timeoutMilliseconds <= 0 && timeoutMilliseconds != Timeout.Infinite)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds", "Timeout must be positive or Timeout.Infinite");

            if (!cancelToken.HasValue)
                cancelToken = CancellationToken.None;

            TaskCompletionSource<Position> tcs;
            if (!IsListening)
            {
                var m = GetManager();

                if (UIDevice.CurrentDevice.CheckSystemVersion(9, 0))
                {
                    m.AllowsBackgroundLocationUpdates = AllowsBackgroundUpdates;
                }

                if (UIDevice.CurrentDevice.CheckSystemVersion(6, 0))
                {
                    m.PausesLocationUpdatesAutomatically = PausesLocationUpdatesAutomatically;
                }

                tcs = new TaskCompletionSource<Position>(m);
                var singleListener = new GeolocationSingleUpdateDelegate(m, DesiredAccuracy, includeHeading, timeoutMilliseconds, cancelToken.Value);
                m.Delegate = singleListener;

                m.StartUpdatingLocation();
                if (includeHeading && SupportsHeading)
                    m.StartUpdatingHeading();

                return singleListener.Task;
            }


            tcs = new TaskCompletionSource<Position>();
            if (position == null)
            {
                EventHandler<PositionErrorEventArgs> gotError = null;
                gotError = (s, e) =>
                {
                    tcs.TrySetException(new GeolocationException(e.Error));
                    PositionError -= gotError;
                };

                PositionError += gotError;

                EventHandler<PositionEventArgs> gotPosition = null;
                gotPosition = (s, e) =>
                {
                    tcs.TrySetResult(e.Position);
                    PositionChanged -= gotPosition;
                };

                PositionChanged += gotPosition;
            }
            else
                tcs.SetResult(position);
            

            return tcs.Task;
        }

        bool CanDeferLocationUpdate { get { return UIDevice.CurrentDevice.CheckSystemVersion(6, 0); } }

        /// <inheritdoc/>
        public Task<bool> StartListeningAsync(int minTime, double minDistance, bool includeHeading = false, EnergySettings energySettings = null)
        {
            this.energySettings = energySettings;
			
            if (minTime < 0)
                throw new ArgumentOutOfRangeException("minTime");
            if (minDistance < 0)
                throw new ArgumentOutOfRangeException("minDistance");
            if (isListening)
                throw new InvalidOperationException("Already listening");

            double desiredAccuracy = DesiredAccuracy;

            // to use deferral, CLLocationManager.DistanceFilter must be set to CLLocationDistance.None, and CLLocationManager.DesiredAccuracy must be 
            // either CLLocation.AccuracyBest or CLLocation.AccuracyBestForNavigation. deferral only available on iOS 6.0 and above.
            if ((this.energySettings?.DeferLocationUpdates ?? false) && CanDeferLocationUpdate)
            {
                minDistance = CLLocationDistance.FilterNone;
                desiredAccuracy = CLLocation.AccuracyBest;
            }

            isListening = true;
            manager.DesiredAccuracy = desiredAccuracy;
            manager.DistanceFilter = minDistance;

            if (this.energySettings?.ListenForSignificantChanges ?? false)
                manager.StartMonitoringSignificantLocationChanges();
            else
                manager.StartUpdatingLocation();

            if (includeHeading && CLLocationManager.HeadingAvailable)
                manager.StartUpdatingHeading();

            return Task.FromResult(true);
        }

        /// <inheritdoc/>
        public Task<bool> StopListeningAsync()
        {
            if (!isListening)
                return Task.FromResult(true);

            isListening = false;
            if (CLLocationManager.HeadingAvailable)
                manager.StopUpdatingHeading();

            // it looks like deferred location updates can apply to the standard service or significant change service. disallow deferral in either case.
            if ((energySettings?.DeferLocationUpdates ?? false) && CanDeferLocationUpdate)
                manager.DisallowDeferredLocationUpdates();
			
            if (energySettings?.ListenForSignificantChanges ?? false)
                manager.StopMonitoringSignificantLocationChanges();
            else
                manager.StopUpdatingLocation();

            energySettings = null;
            position = null;

            return Task.FromResult(true);
        }

        readonly CLLocationManager manager;
        bool isListening;
        Position position;

        CLLocationManager GetManager()
        {
            CLLocationManager m = null;
            new NSObject().InvokeOnMainThread(() => m = new CLLocationManager());
            return m;
        }

        void OnUpdatedHeading(object sender, CLHeadingUpdatedEventArgs e)
        {
            if (e.NewHeading.TrueHeading == -1)
                return;

            var p = (position == null) ? new Position() : new Position(this.position);

            p.Heading = e.NewHeading.TrueHeading;

            this.position = p;

            OnPositionChanged(new PositionEventArgs(p));
        }

        void OnLocationsUpdated(object sender, CLLocationsUpdatedEventArgs e)
        {
            foreach (CLLocation location in e.Locations)
                UpdatePosition(location);

            // defer future location updates if requested
            if ((energySettings?.DeferLocationUpdates ?? false) && !deferringUpdates && CanDeferLocationUpdate)
            {
                manager.AllowDeferredLocationUpdatesUntil(energySettings.DeferralDistanceMeters == null ? CLLocationDistance.MaxDistance : energySettings.DeferralDistanceMeters.GetValueOrDefault(), 
                    energySettings.DeferralTime == null ? CLLocationManager.MaxTimeInterval : energySettings.DeferralTime.GetValueOrDefault().TotalSeconds);
                deferringUpdates = true;
            }			
        }

        void OnUpdatedLocation(object sender, CLLocationUpdatedEventArgs e)
        {
            UpdatePosition(e.NewLocation);
        }

        void UpdatePosition(CLLocation location)
        {
            var p = (position == null) ? new Position() : new Position(this.position);

            if (location.HorizontalAccuracy > -1)
            {
                p.Accuracy = location.HorizontalAccuracy;
                p.Latitude = location.Coordinate.Latitude;
                p.Longitude = location.Coordinate.Longitude;
            }

            if (location.VerticalAccuracy > -1)
            {
                p.Altitude = location.Altitude;
                p.AltitudeAccuracy = location.VerticalAccuracy;
            }

            if (location.Speed > -1)
                p.Speed = location.Speed;

            var dateTime = location.Timestamp.ToDateTime().ToUniversalTime();
            p.Timestamp = new DateTimeOffset(dateTime);

            position = p;

            OnPositionChanged(new PositionEventArgs(p));

            location.Dispose();
        }


        void OnFailed(object sender, NSErrorEventArgs e)
        {
            if ((CLError)(int)e.Error.Code == CLError.Network)
                OnPositionError(new PositionErrorEventArgs(GeolocationError.PositionUnavailable));
        }

        void OnAuthorizationChanged(object sender, CLAuthorizationChangedEventArgs e)
        {
            if (e.Status == CLAuthorizationStatus.Denied || e.Status == CLAuthorizationStatus.Restricted)
                OnPositionError(new PositionErrorEventArgs(GeolocationError.Unauthorized));
        }

        void OnPositionChanged(PositionEventArgs e) => PositionChanged?.Invoke(this, e);


        async void OnPositionError(PositionErrorEventArgs e)
        {
            await StopListeningAsync();
            PositionError?.Invoke(this, e);
        }
    }
}