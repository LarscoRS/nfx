/*<FILE_LICENSE>
* NFX (.NET Framework Extension) Unistack Library
* Copyright 2003-2014 Dmitriy Khmaladze, IT Adapter Inc / 2015-2016 Aum Code LLC
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
</FILE_LICENSE>*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

using NFX;
using NFX.Environment;
using NFX.Financial;
using NFX.Security;
using NFX.Serialization.JSON;

namespace NFX.Web.Pay.Braintree
{
  public sealed class BraintreeSystem : PaySystem
  {
    #region CONSTS
    public const string MERCHANTS_URI = "/merchants/{0}";

    private const string HDR_AUTHORIZATION = "Authorization";
    private const string HDR_AUTHORIZATION_BASIC = "Basic {0}";
    private const string HDR_AUTHORIZATION_OAUTH = "Bearer {0}";

    private const string BASIC_AUTH_FORMAT = "{0}:{1}";

    private const string HDR_X_API_VERSION = "X-ApiVersion";
    public const string API_VERSION = "4";

    public Uri URI_ClientToken(string merchantID) { return new Uri(m_ApiUri, MERCHANTS_URI.Args(merchantID) + "/client_token"); }
    public Uri URI_Transactions(string merchantID) { return new Uri(m_ApiUri, MERCHANTS_URI.Args(merchantID) + "/transactions"); }
    public Uri URI_SubmitForSettlement(string merchantID, string id)
    {
      return new Uri(m_ApiUri, MERCHANTS_URI.Args(merchantID) + "/transactions/{0}/submit_for_settlement".Args(id));
    }
    public Uri URI_Customer(string merchantID, string customerID) { return new Uri(m_ApiUri, MERCHANTS_URI.Args(merchantID) + "/customers/" + customerID); }
    #endregion

    #region .ctor
    public BraintreeSystem(string name, IConfigSectionNode node) : this((Uri)null, name, node, null) { }
    public BraintreeSystem(string name, IConfigSectionNode node, object director) : this((Uri)null, name, node, director) { }
    public BraintreeSystem(string apiUri, string name, IConfigSectionNode node, object director) : this(apiUri.IsNotNullOrWhiteSpace() ? new Uri(apiUri) : null, name, node, director) {}
    public BraintreeSystem(Uri apiUri, string name, IConfigSectionNode node, object director) : base(name, node, director)
    { if (apiUri != null) m_ApiUri = apiUri; }
    #endregion

    #region fields
    private BraintreeWebTerminal m_WebTerminal;
    [Config]
    private Uri m_ApiUri;
    #endregion


    #region Properties
    public override string ComponentCommonName { get { return "braintreepayments"; } }

    public override IPayWebTerminal WebTerminal
    {
      get
      {
        if (m_WebTerminal == null)
          m_WebTerminal = new BraintreeWebTerminal(this);
        return m_WebTerminal;
      }
    }
    #endregion

    public object GenerateClientToken(PaySession session)
    {
      return GenerateClientToken((BraintreeSession)session);
    }

    public object GenerateClientToken(BraintreeSession session)
    {

      dynamic response = getResponse(session, URI_ClientToken(session.MerchantID),
      new { client_token = new { version = "2" } });
      return response.clientToken.value;
    }

    public override Transaction Charge(PaySession session, ITransactionContext context, Account from, Account to, Amount amount, bool capture = true, string description = null, object extraData = null)
    {
      return Charge((BraintreeSession)session, context, from, to, amount, capture, description, extraData);
    }

    public Transaction Charge(BraintreeSession session, ITransactionContext context, Account from, Account to, Amount amount, bool capture = true, string description = null, object extraData = null)
    {
      var orderContex = context as IOrderTransactionContext;

      var fromActualData = PaySystemHost.AccountToActualData(context, from);

      try
      {
        var isWeb = fromActualData.Account.IsWebTerminalToken;
        var request = new JSONDataMap();

        var transaction = (JSONDataMap)(request["transaction"] = new JSONDataMap());
        transaction["type"] = "sale";
        transaction["amount"] = amount.Value;
        transaction[isWeb ? "payment_method_nonce" : "payment_method_token"] = fromActualData.Account.AccountID;
        if (orderContex != null)
        {
          transaction["order_id"] = orderContex.OrderId;
          if (orderContex.IsNewCustomer)
          {
            var customer = (JSONDataMap)(transaction["customer"] = new JSONDataMap());
            customer["id"] = orderContex.CustomerId;
          }
          else
          {
            try
            {
              check(session, URI_Customer(session.MerchantID, orderContex.CustomerId.AsString()));
              transaction["customer_id"] = orderContex.CustomerId;
            } catch
            {
              var customer = (JSONDataMap)(transaction["customer"] = new JSONDataMap());
              customer["id"] = orderContex.CustomerId;
            }
          }
        }

        var billing = (JSONDataMap)(transaction["billing"] = new JSONDataMap());
        if (fromActualData.FirstName.IsNotNullOrWhiteSpace())
          billing["first_name"] = fromActualData.FirstName;
        if (fromActualData.LastName.IsNotNullOrWhiteSpace())
          billing["last_name"] = fromActualData.LastName;
        if (fromActualData.BillingAddress.Address1.IsNotNullOrWhiteSpace())
          billing["street_address"] = fromActualData.BillingAddress.Address1;
        if (fromActualData.BillingAddress.Address2.IsNotNullOrWhiteSpace())
          billing["extended_address"] = fromActualData.BillingAddress.Address2;
        if (fromActualData.BillingAddress.City.IsNotNullOrWhiteSpace())
          billing["locality"] = fromActualData.BillingAddress.City;
        if (fromActualData.BillingAddress.Country.IsNotNullOrWhiteSpace())
          billing["country_code_alpha3"] = fromActualData.BillingAddress.Country;
        if (fromActualData.BillingAddress.Region.IsNotNullOrWhiteSpace())
          billing["region"] = fromActualData.BillingAddress.Region;
        if (fromActualData.BillingAddress.PostalCode.IsNotNullOrWhiteSpace())
          billing["postal_code"] = fromActualData.BillingAddress.PostalCode;
        if (fromActualData.BillingAddress.Company.IsNotNullOrWhiteSpace())
          billing["company"] = fromActualData.BillingAddress.Company;

        var options = (JSONDataMap)(transaction["options"] = new JSONDataMap());
        options["submit_for_settlement"] = capture;
        if (orderContex != null)
          options["store_in_vault_on_success"] = isWeb;

        dynamic obj = getResponse(session, URI_Transactions(session.MerchantID), request);

        string created = obj.transaction.createdAt;

        var taId = PaySystemHost.GenerateTransactionID(session, context, TransactionType.Charge);

        string customerID = obj.transaction.customer.id;
        string token = obj.transaction.creditCard.token;
        string transactionID = obj.transaction.id;

        var ta = Transaction.Charge(taId, this.Name, "{0}:{1}:{2}".Args(customerID, token, transactionID), from, to, amount, created.AsDateTime(), description);

        StatCharge(amount);

        return ta;
      }
      catch (Exception ex)
      {
        StatChargeError();

        var wex = ex as System.Net.WebException;
        if (wex != null)
        {
          using (var sr = new System.IO.StreamReader(wex.Response.GetResponseStream()))
          {
            var respStr = sr.ReadToEnd();
            var resp = respStr.IsNotNullOrWhiteSpace() ? respStr.JSONToDynamic() : null;
            if (resp != null)
              throw new PaymentException(resp.apiErrorResponse.message, ex);
          }

        }

        throw new PaymentException(StringConsts.PAYMENT_CANNOT_CAPTURE_CAPTURED_PAYMENT_ERROR + this.GetType()
          + " .Charge(session='{0}', card='{1}', amount='{2}')".Args(session, from, amount), ex);
      }
    }

    public override void Capture(PaySession session, ITransactionContext context, ref Transaction charge, Amount? amount = default(Amount?), string description = null, object extraData = null)
    {
      Capture((BraintreeSession)session, context, ref charge, amount, description, extraData);
    }

    public void Capture(BraintreeSession session, ITransactionContext context, ref Transaction charge, Amount? amount = default(Amount?), string description = null, object extraData = null)
    {
      try
      {
        var splitTran = charge.ProcessorToken.AsString().Split(':');

        var request = new JSONDataMap();
        var transaction = (JSONDataMap)(request["transaction"] = new JSONDataMap());
        if (amount.HasValue)
          transaction["amount"] = amount.Value.Value;

        dynamic obj = getResponse(session, URI_SubmitForSettlement(session.MerchantID, splitTran[2]), request, HTTPRequestMethod.PUT);

        StatCapture(charge, amount);
      }
      catch (Exception ex)
      {
        StatChargeError();

        var wex = ex as System.Net.WebException;
        if (wex != null)
        {
          using (var sr = new System.IO.StreamReader(wex.Response.GetResponseStream()))
          {
            var respStr = sr.ReadToEnd();
            var resp = respStr.IsNotNullOrWhiteSpace() ? respStr.JSONToDynamic() : null;
            if (resp != null)
              throw new PaymentException(resp.apiErrorResponse.message, ex);
          }

        }

        throw new PaymentException(StringConsts.PAYMENT_CANNOT_CHARGE_PAYMENT_ERROR + this.GetType()
          + " .Capture(session='{0}', charge='{1}', amount='{2}')".Args(session, charge, amount), ex);
      }
    }

    public override Transaction Refund(PaySession session, ITransactionContext context, ref Transaction charge, Amount? amount = default(Amount?), string description = null, object extraData = null)
    {
      throw new NotImplementedException();
    }

    public override Transaction Transfer(PaySession session, ITransactionContext context, Account from, Account to, Amount amount, string description = null, object extraData = null)
    {
      throw new NotImplementedException();
    }

    public override PaymentException VerifyPotentialTransaction(PaySession session, ITransactionContext context, bool transfer, IActualAccountData from, IActualAccountData to, Amount amount)
    {
      throw new NotImplementedException();
    }

    protected override PaySession DoStartSession(PayConnectionParameters cParams = null)
    {
      return new BraintreeSession(this, (BraintreeConnectionParameters)(cParams ?? DefaultSessionConnectParams));
    }

    protected override PayConnectionParameters MakeDefaultSessionConnectParams(IConfigSectionNode paramsSection)
    {
      return PayConnectionParameters.Make<BraintreeConnectionParameters>(paramsSection);
    }

    #region .pvt
    private JSONDynamicObject getResponse(BraintreeSession session, Uri uri, object body = null, HTTPRequestMethod method = HTTPRequestMethod.POST)
    {
      if (!session.IsValid)
        throw new PaymentException("Braintree: " + StringConsts.PAYMENT_BRAINTREE_SESSION_INVALID.Args(this.GetType().Name + ".getResponse"));

      var prms = new WebClient.RequestParams()
      {
        Caller = this,
        Uri = uri,
        Method = method,
        ContentType = ContentType.JSON,
        AcceptType = ContentType.JSON,
        Headers = new Dictionary<string, string>()
        {
          { HDR_AUTHORIZATION, getAuthHeader(session.User.Credentials) },
          { HDR_X_API_VERSION, API_VERSION }
        },
        Body = body != null ? body.ToJSON(JSONWritingOptions.Compact) : null
      };

      return WebClient.GetJsonAsDynamic(prms);
    }

    private string check(BraintreeSession session, Uri uri)
    {
      if (!session.IsValid)
        throw new PaymentException("Braintree: " + StringConsts.PAYMENT_BRAINTREE_SESSION_INVALID.Args(this.GetType().Name + ".check"));

      var prms = new WebClient.RequestParams()
      {
        Caller = this,
        Uri = uri,
        Method = HTTPRequestMethod.GET,
        Headers = new Dictionary<string, string>()
        {
          { HDR_AUTHORIZATION, getAuthHeader(session.User.Credentials) },
          { HDR_X_API_VERSION, API_VERSION }
        }
      };

      return WebClient.GetString(prms);
    }

    private string getAuthHeader(Credentials credentials)
    {
      if (credentials is BraintreeCredentials)
        return HDR_AUTHORIZATION_BASIC.Args(getBasicAuthString((credentials as BraintreeCredentials)));
      if (credentials is BraintreeAuthCredentials)
        return HDR_AUTHORIZATION_OAUTH.Args((credentials as BraintreeAuthCredentials).AccessToken);
      throw new NFXException("{0}.getAuthHeader({1})".Args(GetType().Name, credentials.GetType().Name));
    }

    private string getBasicAuthString(BraintreeCredentials braintreeCredentials)
    {
      var bytes = Encoding.UTF8.GetBytes(BASIC_AUTH_FORMAT.Args(braintreeCredentials.PublicKey, braintreeCredentials.PrivateKey));
      return Convert.ToBase64String(bytes);
    }
    #endregion
  }
}
