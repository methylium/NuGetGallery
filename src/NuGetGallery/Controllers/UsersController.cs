﻿using System;
using System.Linq;
using System.Net.Mail;
using System.Security.Principal;
using System.Web.Mvc;
using NuGetGallery.Configuration;

namespace NuGetGallery
{
    public partial class UsersController : AppController
    {
        public ICuratedFeedService CuratedFeedService { get; protected set; }
        public IPrincipal CurrentUser { get; protected set; }
        public IMessageService MessageService { get; protected set; }
        public IPackageService PackageService { get; protected set; }
        public IUserService UserService { get; protected set; }
        public IAppConfiguration Config { get; protected set; }

        protected UsersController() { }

        public UsersController(
            ICuratedFeedService feedsQuery,
            IUserService userService,
            IPackageService packageService,
            IMessageService messageService,
            IPrincipal currentUser,
            IAppConfiguration config) : this()
        {
            CuratedFeedService = feedsQuery;
            UserService = userService;
            PackageService = packageService;
            MessageService = messageService;
            CurrentUser = currentUser;
            Config = config;
        }

        [Authorize]
        public virtual ActionResult Account()
        {
            var user = UserService.FindByUsername(CurrentUser.Identity.Name);
            var curatedFeeds = CuratedFeedService.GetFeedsForManager(user.Key);
            return View(
                new AccountViewModel
                    {
                        ApiKey = user.ApiKey.ToString(),
                        CuratedFeeds = curatedFeeds.Select(cf => cf.Name)
                    });
        }

        [Authorize]
        public virtual ActionResult Edit()
        {
            var user = UserService.FindByUsername(CurrentUser.Identity.Name);
            var model = new EditProfileViewModel
                {
                    EmailAddress = user.EmailAddress,
                    EmailAllowed = user.EmailAllowed,
                    PendingNewEmailAddress = user.UnconfirmedEmailAddress
                };
            return View(model);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public virtual ActionResult Edit(EditProfileViewModel profile)
        {
            if (ModelState.IsValid)
            {
                var user = UserService.FindByUsername(CurrentUser.Identity.Name);
                if (user == null)
                {
                    return HttpNotFound();
                }

                string existingConfirmationToken = user.EmailConfirmationToken;
                try
                {
                    UserService.UpdateProfile(user, profile.EmailAddress, profile.EmailAllowed);
                }
                catch (EntityException ex)
                {
                    ModelState.AddModelError(String.Empty, ex.Message);
                    return View(profile);
                }

                if (existingConfirmationToken == user.EmailConfirmationToken)
                {
                    TempData["Message"] = "Account settings saved!";
                }
                else
                {
                    TempData["Message"] =
                        "Account settings saved! We sent a confirmation email to verify your new email. When you confirm the email address, it will take effect and we will forget the old one.";

                    var confirmationUrl = Url.ConfirmationUrl(
                        MVC.Users.Confirm(), user.Username, user.EmailConfirmationToken, protocol: Request.Url.Scheme);
                    MessageService.SendEmailChangeConfirmationNotice(new MailAddress(profile.EmailAddress, user.Username), confirmationUrl);
                }

                return RedirectToAction(MVC.Users.Account());
            }
            return View(profile);
        }

        [Authorize]
        public virtual ActionResult Packages()
        {
            var user = UserService.FindByUsername(CurrentUser.Identity.Name);
            var packages = PackageService.FindPackagesByOwner(user);

            var model = new ManagePackagesViewModel
                {
                    Packages = from p in packages
                               select new PackageViewModel(p)
                                   {
                                       DownloadCount = p.PackageRegistration.DownloadCount,
                                       Version = null
                                   },
                };
            return View(model);
        }

        [Authorize]
        [ValidateAntiForgeryToken]
        [HttpPost]
        public virtual ActionResult GenerateApiKey()
        {
            UserService.GenerateApiKey(CurrentUser.Identity.Name);
            return RedirectToAction(MVC.Users.Account());
        }

        public virtual ActionResult ForgotPassword()
        {
            // We don't want Login to have us as a return URL
            // By having this value present in the dictionary BUT null, we don't put "returnUrl" on the Login link at all
            ViewData[Constants.ReturnUrlViewDataKey] = null;
            
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public virtual ActionResult ForgotPassword(ForgotPasswordViewModel model)
        {
            // We don't want Login to have us as a return URL
            // By having this value present in the dictionary BUT null, we don't put "returnUrl" on the Login link at all
            ViewData[Constants.ReturnUrlViewDataKey] = null;

            // Note, the user may not yet have a confirmed email address. That's OK.
            // But what should we do in this case?
            // a) we could request account confirmation, and then make them go through that before they can do password reset again... (frustrating)
            // or b) we could just send them a password reset request, and they can do the email confirmation later, whenever they really need it (e.g. upload package/contact owners)
            // b) seems clearly better.
            //
            // When should we trust such a request, and who should get the email?
            // a) the email address is attached to a NuGet user account as an unconfirmed email address
            // b) that particular email address isn't confirmed as belonging to any other NuGet user.
            if (ModelState.IsValid)
            {
                // TODO:
                var user = UserService.GeneratePasswordResetToken(model.Email, Constants.DefaultPasswordResetTokenExpirationHours * 60);
                if (user != null)
                {
                    var resetPasswordUrl = Url.ConfirmationUrl(
                        MVC.Users.ResetPassword(), user.Username, user.PasswordResetToken, protocol: Request.Url.Scheme);
                    MessageService.SendPasswordResetInstructions(user, resetPasswordUrl);

                    TempData["Email"] = user.EmailAddress;
                    return RedirectToAction(MVC.Users.PasswordSent());
                }

                ModelState.AddModelError("Email", "Could not find anyone with that email.");
            }

            return View(model);
        }

        public virtual ActionResult ResendConfirmation()
        {
            // We don't want Login to have us as a return URL
            // By having this value present in the dictionary BUT null, we don't put "returnUrl" on the Login link at all
            ViewData[Constants.ReturnUrlViewDataKey] = null;
            
            return View();
        }

        [Authorize]
        public virtual ActionResult ConfirmationRequired(string userAction, string returnUrl)
        {
            // I think it should be obvious why we don't want the current URL to be the return URL here ;)
            ViewData[Constants.ReturnUrlViewDataKey] = returnUrl;

            User user = _userService.FindByUsername(HttpContext.User.Identity.Name);
            if (user == null)
            {
                return new HttpStatusCodeResult(403);
            }

            if (!String.IsNullOrEmpty(user.EmailAddress))
            {
                // How did you get here? Never mind!
                return new RedirectResult(RedirectHelper.SafeRedirectUrl(returnUrl));
            }

            var model = new ConfirmationRequiredViewModel
            {
                MailSent = false,
                EmailAddress = user.EmailAddress,
                UserAction = userAction,
                ReturnUrl = returnUrl,
            };

            return View(model);
        }

        [HttpPost]
        [Authorize]
        [ActionName("ConfirmationRequired")]
        public virtual ActionResult ConfirmationRequiredPost(string userAction, string returnUrl)
        {
            // I think it should be obvious why we don't want the current URL to be the return URL here ;)
            ViewData[Constants.ReturnUrlViewDataKey] = returnUrl;

            // Passing in scheme to force fully qualified URL
            var confirmationUrl = Url.ConfirmationUrl(
                MVC.Users.Confirm(), user.Username, user.EmailConfirmationToken, protocol: Request.Url.Scheme);

            MessageService.SendConfirmationEmail(new MailAddress(user.UnconfirmedEmailAddress), user.Username, confirmationUrl);

            var model = new ConfirmationRequiredViewModel
            {
                MailSent = true,
                EmailAddress = user.EmailAddress,
                UserAction = userAction,
                ReturnUrl = returnUrl,
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public virtual ActionResult ResendConfirmation(ResendConfirmationEmailViewModel model)
        {
            // We don't want Login to have us as a return URL
            // By having this value present in the dictionary BUT null, we don't put "returnUrl" on the Login link at all
            ViewData[Constants.ReturnUrlViewDataKey] = null;

            if (ModelState.IsValid)
            {
                var usersClaimingEmailAddress = UserService.FindByUnconfirmedEmailAddress(model.Email, model.Username);

                if (usersClaimingEmailAddress.Count == 1)
                {
                    var user = usersClaimingEmailAddress.SingleOrDefault();
                    var confirmationUrl = Url.ConfirmationUrl(
                        MVC.Users.Confirm(), user.Username, user.EmailConfirmationToken, protocol: Request.Url.Scheme);
                    MessageService.SendConfirmationEmail(new MailAddress(user.UnconfirmedEmailAddress, user.Username), confirmationUrl);
                    return RedirectToAction(MVC.Users.ConfirmationMailSent());
                }
                else if (usersClaimingEmailAddress.Count > 1)
                {
                    ModelState.AddModelError("Username", "Multiple users registered with your email address. Enter your username in order to resend confirmation email.");
                }
                else
                {
                    ModelState.AddModelError("Email", "There was an issue resending your confirmation token.");
                }
            }
            return View(model);
        }

        public virtual ActionResult ConfirmationMailSent()
        {
            return View();
        }

        public virtual ActionResult PasswordSent()
        {
            // We don't want Login to have us as a return URL
            // By having this value present in the dictionary BUT null, we don't put "returnUrl" on the Login link at all
            ViewData[Constants.ReturnUrlViewDataKey] = null;
            
            ViewBag.Email = TempData["Email"];
            ViewBag.Expiration = Constants.DefaultPasswordResetTokenExpirationHours;
            return View();
        }

        public virtual ActionResult ResetPassword()
        {
            // We don't want Login to have us as a return URL
            // By having this value present in the dictionary BUT null, we don't put "returnUrl" on the Login link at all
            ViewData[Constants.ReturnUrlViewDataKey] = null;
            
            ViewBag.ResetTokenValid = true;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public virtual ActionResult ResetPassword(string username, string token, PasswordResetViewModel model)
        {
            // We don't want Login to have us as a return URL
            // By having this value present in the dictionary BUT null, we don't put "returnUrl" on the Login link at all
            ViewData[Constants.ReturnUrlViewDataKey] = null;
            
            ViewBag.ResetTokenValid = UserService.ResetPasswordWithToken(username, token, model.NewPassword);

            if (!ViewBag.ResetTokenValid)
            {
                ModelState.AddModelError("", "The Password Reset Token is not valid or expired.");
                return View(model);
            }
            return RedirectToAction(MVC.Users.PasswordChanged());
        }

        public virtual ActionResult Confirm(string username, string token)
        {
            // We don't want Login to have us as a return URL
            // By having this value present in the dictionary BUT null, we don't put "returnUrl" on the Login link at all
            ViewData[Constants.ReturnUrlViewDataKey] = null;
            
            if (String.IsNullOrEmpty(token))
            {
                return HttpNotFound();
            }
            var user = UserService.FindByUsername(username);
            if (user == null)
            {
                return HttpNotFound();
            }

            string existingEmail = user.EmailAddress;
            var model = new EmailConfirmationModel
                {
                    ConfirmingNewAccount = String.IsNullOrEmpty(existingEmail),
                    SuccessfulConfirmation = UserService.ConfirmEmailAddress(user, token)
                };

            // SuccessfulConfirmation is required so that the confirm Action isn't a way to spam people.
            // Change notice not required for new accounts.
            if (model.SuccessfulConfirmation && !model.ConfirmingNewAccount)
            {
                MessageService.SendEmailChangeNoticeToPreviousEmailAddress(user, existingEmail);
            }
            return View(model);
        }

        public virtual ActionResult Profiles(string username)
        {
            var user = UserService.FindByUsername(username);
            if (user == null)
            {
                return HttpNotFound();
            }

            var packages = (from p in PackageService.FindPackagesByOwner(user)
                            where p.Listed
                            orderby p.Version descending
                            group p by p.PackageRegistration.Id)
                .Select(c => new PackageViewModel(c.First()))
                .ToList();

            var model = new UserProfileModel
                {
                    Username = user.Username,
                    EmailAddress = user.EmailAddress,
                    Packages = packages,
                    TotalPackageDownloadCount = packages.Sum(p => p.TotalDownloadCount)
                };

            return View(model);
        }

        [Authorize]
        public virtual ActionResult ChangePassword()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public virtual ActionResult ChangePassword(PasswordChangeViewModel model)
        {
            if (ModelState.IsValid)
            {
                if (!UserService.ChangePassword(CurrentUser.Identity.Name, model.OldPassword, model.NewPassword))
                {
                    ModelState.AddModelError(
                        "OldPassword",
                        Strings.CurrentPasswordIncorrect);
                }
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            return RedirectToAction(MVC.Users.PasswordChanged());
        }

        public virtual ActionResult PasswordChanged()
        {
            return View();
        }
    }
}
