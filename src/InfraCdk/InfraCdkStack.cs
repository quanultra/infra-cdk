using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Constructs;

namespace InfraCdk
{
    public class InfraCdkStack : Stack
    {
        internal InfraCdkStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            var vpc = new Vpc(this, "MyVPC", new VpcProps
            {
                MaxAzs = 2
            });
        }
    }
}
