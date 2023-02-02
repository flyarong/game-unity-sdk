using AnkrSDK.WalletConnectSharp.Core;

namespace AnkrSDK.WalletConnectSharp.Unity.Events
{
    public class TransportConnectedTransition : WalletConnectTransitionBase
    {
        public TransportConnectedTransition(WalletConnectSession session, WalletConnectStatus previousStatus, WalletConnectStatus newStatus)
            : base(session, previousStatus, newStatus)
        {
            
        }
    }
}