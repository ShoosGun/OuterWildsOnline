using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OuterWildsOnline.SyncObjects
{
    public class RaftToSendSync : ObjectToSendSync
    {
        protected override void Awake()
        {
            //interpolate = false;
            //GlobalMessenger<SurveyorProbe>.AddListener("LaunchProbe", new Callback<SurveyorProbe>(this.OnLaunchProbe));
            //GlobalMessenger<SurveyorProbe>.AddListener("RetrieveProbe", new Callback<SurveyorProbe>(this.OnProbeRetrieved));

            SetObjectName("Raft");
            base.Awake();
            //ObjectData.PutBool("enable", gameObject.activeSelf);
        }
    }
}
