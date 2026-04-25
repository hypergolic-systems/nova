namespace Nova.Core.Resources;

public class Buffer {
  public Resource Resource;
  public double Capacity;
  public double Contents;
  public double MaxRateIn;
  public double MaxRateOut;
  public double Rate;

  public void FlowLimits(double rateIn, double rateOut) {
    MaxRateIn = rateIn;
    MaxRateOut = rateOut;
  }

  public void Integrate(double deltaT) {
    Contents += Rate * deltaT;
    if (Contents < 0) Contents = 0;
    else if (Contents > Capacity) Contents = Capacity;
  }
}
