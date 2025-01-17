﻿using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using VpnHood.Common.Exceptions;

namespace VpnHood.Server
{
    public class SslCertificateManager
    {
        private readonly IAccessServer _accessServer;
        private readonly ConcurrentDictionary<IPEndPoint, X509Certificate2> _certificates = new();
        private readonly Lazy<X509Certificate2> _maintenanceCertificate = new(InitMaintenanceCertificate);
        private readonly TimeSpan _maintenanceCheckInterval;
        private DateTime _lastMaintenanceTime = DateTime.Now;

        public SslCertificateManager(IAccessServer accessServer, TimeSpan maintenanceCheckInterval)
        {
            _maintenanceCheckInterval = maintenanceCheckInterval;
            _accessServer = accessServer;
        }

        private static X509Certificate2 InitMaintenanceCertificate()
        {
            var subjectName = $"CN={CertificateUtil.CreateRandomDns()}, OU=MT";
            using var cert = CertificateUtil.CreateSelfSigned(subjectName);

            // it is required to set X509KeyStorageFlags
            var ret = new X509Certificate2(cert.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.Exportable);
            return ret;
        }

        public async Task<X509Certificate2> GetCertificate(IPEndPoint ipEndPoint)
        {
            // check maintenance mode
            if (_accessServer.IsMaintenanceMode && (DateTime.Now - _lastMaintenanceTime) < _maintenanceCheckInterval)
                return _maintenanceCertificate.Value;

            // find in cache
            if (_certificates.TryGetValue(ipEndPoint, out var certificate))
                return certificate;

            // get from access server
            try
            {
                var certificateData = await _accessServer.GetSslCertificateData(ipEndPoint);
                certificate = new X509Certificate2(certificateData);
                if (_maintenanceCheckInterval != TimeSpan.Zero) // test mode should not use cache
                    _certificates.TryAdd(ipEndPoint, certificate);
                return certificate;
            }
            catch (MaintenanceException)
            {
                ClearCache();
                _lastMaintenanceTime = DateTime.Now;
                return _maintenanceCertificate.Value;
            }
        }

        public void ClearCache()
        {
            foreach (var item in _certificates.Values)
                item.Dispose();
            _certificates.Clear();
        }
    }
}